#!/usr/bin/env python3
"""
Local Flash-compatible backend for MyGameBuilder.
Provides Rails-style auth endpoints and S3 SOAP piece storage emulation.
Run: python server/mgb_local_server.py
"""

from flask import Flask, request, Response, send_from_directory
import sqlite3
import hashlib
import base64
import xml.etree.ElementTree as ET
from xml.sax.saxutils import escape
from datetime import datetime
import json
import os
import socket
import subprocess
import sys
import signal
import time
from pathlib import Path

from archive import Archive, OverlayArchive, BUCKET as ARCHIVE_BUCKET
from bootstrap import bootstrap_bundle

app = Flask(__name__)
HOST = '0.0.0.0'
PORT = 3000

ROOT_DIR = Path(__file__).resolve().parent.parent
CLIENT_DIR = ROOT_DIR / 'client'
SWF_NAME = 'MGB.swf'

DB_DIR = ROOT_DIR / 'server'
DB_PATH = str(DB_DIR / 'mgb_local.db')

# Filesystem-backed S3 emulation. The snapshot in ``archive/`` is
# read-only; every backend write lands in ``data/``. Deletes of
# archive-only entries leave the archive untouched and drop a tombstone
# in ``data/`` so reads see them as gone.
ARCHIVE_ROOT = Path(os.environ.get('MGB_ARCHIVE_ROOT', ROOT_DIR / 'archive'))
DATA_ROOT = Path(os.environ.get('MGB_DATA_ROOT', ROOT_DIR / 'data'))

# Make sure the client (required) and archive (optional) bundles are
# present on disk before anything else touches them. This will exit
# with a clear error if the client URL hasn't been configured yet.
bootstrap_bundle(
    url_file=ROOT_DIR / 'client-download.url',
    target_dir=CLIENT_DIR,
    label='client',
    required=True,
)
bootstrap_bundle(
    url_file=ROOT_DIR / 'archive-download.url',
    target_dir=ARCHIVE_ROOT,
    label='archive',
    required=False,
)

archive = OverlayArchive(ARCHIVE_ROOT, DATA_ROOT)

# XML/SOAP namespace constants
SOAP_ENV_NS = 'http://schemas.xmlsoap.org/soap/envelope/'
SOAP_ENC_NS = 'http://schemas.xmlsoap.org/soap/encoding/'
XSD_NS = 'http://www.w3.org/1999/XMLSchema'
XSI_NS = 'http://www.w3.org/1999/XMLSchema-instance'
AWS_NS = 'http://s3.amazonaws.com/doc/2006-03-01/'

# ===== Database initialization =====

def init_db():
    """Initialize SQLite database with schema and seed data."""
    DB_DIR.mkdir(parents=True, exist_ok=True)
    conn = sqlite3.connect(DB_PATH)
    c = conn.cursor()

    # Users table
    c.execute('''
        CREATE TABLE IF NOT EXISTS users (
            login TEXT PRIMARY KEY,
            password TEXT NOT NULL,
            email TEXT,
            secret_question TEXT,
            secret_answer TEXT,
            dob TEXT,
            login_count INTEGER DEFAULT 0,
            created_at TEXT DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT DEFAULT CURRENT_TIMESTAMP
        )
    ''')

    c.execute('''
        CREATE TABLE IF NOT EXISTS game_stats (
            username TEXT NOT NULL,
            gamename TEXT NOT NULL,
            plays_counter INTEGER DEFAULT 0,
            completions_counter INTEGER DEFAULT 0,
            rating_graphics_total INTEGER DEFAULT 0,
            rating_graphics_count INTEGER DEFAULT 0,
            rating_gameplay_total INTEGER DEFAULT 0,
            rating_gameplay_count INTEGER DEFAULT 0,
            gamestatus INTEGER DEFAULT 0,
            gametype INTEGER DEFAULT 0,
            gamegenre TEXT DEFAULT '',
            description TEXT DEFAULT '',
            updated_at TEXT DEFAULT CURRENT_TIMESTAMP,
            PRIMARY KEY (username, gamename)
        )
    ''')

    # Piece storage used to live in an ``objects`` table here, but the
    # overlay archive (``data/`` over ``archive/``) is now the single
    # source of truth for tiles/actors/maps/profiles/screenshots. The
    # SQLite database only retains accounts and game-stats metadata.

    # Seed users if empty
    c.execute('SELECT COUNT(*) FROM users')
    if c.fetchone()[0] == 0:
        seed_users = [
            ('foo', 'bar', 'foo@bar.com', 'Name of first pet?', 'Perky', '12/31/2006'),
            ('guest', 'guest', 'guest@mgb.local', 'Guest?', 'Yes', '01/01/2000'),
            ('!system', 'system', 'system@mgb.local', 'System?', 'Yes', '01/01/2000'),
        ]
        for login, password, email, sq, sa, dob in seed_users:
            c.execute('''
                INSERT INTO users (login, password, email, secret_question, secret_answer, dob)
                VALUES (?, ?, ?, ?, ?, ?)
            ''', (login, password, email, sq, sa, dob))
        print(f"[DB] Seeded {len(seed_users)} users: foo/bar, guest/guest, !system/system")

    conn.commit()
    conn.close()
    print(f"[DB] Initialized at {DB_PATH}")

# ===== Helper functions =====

def get_db():
    """Get database connection."""
    conn = sqlite3.connect(DB_PATH)
    conn.row_factory = sqlite3.Row
    return conn

def xml_fragment(*elements):
    """Build a simple XML fragment for Flex HTTPService resultFormat=object."""
    return ''.join(elements)

def soap_datetime_now():
    """Return current datetime in SOAP format."""
    return datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%S.000Z')

def xml_escape(value):
    """Escape a value for XML element text."""
    return escape('' if value is None else str(value), {'"': '&quot;', "'": '&apos;'})

def local_name(elem):
    """Return an XML element local name without namespace."""
    return elem.tag.split('}')[-1] if '}' in elem.tag else elem.tag

def infer_piece_type_from_key(key):
    """Infer piece type from a storage key shaped as username/project/piecetype/name."""
    parts = (key or '').split('/')
    return parts[2] if len(parts) >= 4 else 'unknown'

def log_soap_summary(operation, key='', content_length=None, metadata=None, extra=''):
    """Log SOAP request details without dumping payload blobs or credentials."""
    details = [f"Operation: {operation}"]
    if key:
        details.append(f"Key: {key}")
        details.append(f"Type: {infer_piece_type_from_key(key)}")
    if content_length is not None:
        details.append(f"ContentLength: {content_length}")
    if metadata is not None:
        details.append(f"Metadata: {sorted(metadata.keys())}")
    if extra:
        details.append(extra)
    print("[SOAP] " + ", ".join(details))

def describe_port_owner(port):
    """Return best-effort process details for the process listening on a TCP port."""
    if os.name != 'nt':
        return None

    try:
        command = [
            'powershell',
            '-NoProfile',
            '-Command',
            f"$c = Get-NetTCPConnection -LocalPort {port} -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1; "
            "$p = if ($c) { Get-Process -Id $c.OwningProcess -ErrorAction SilentlyContinue }; "
            "if ($c) { [pscustomobject]@{Pid=$c.OwningProcess; Name=$p.ProcessName; Path=$p.Path} | ConvertTo-Json -Compress }",
        ]
        result = subprocess.run(command, capture_output=True, text=True, timeout=5)
        output = result.stdout.strip()
        if not output:
            return None
        return json.loads(output)
    except Exception:
        return None

def ensure_port_available(host, port):
    """Exit with clear instructions if the backend port is already in use."""
    owner = describe_port_owner(port)
    if owner:
        handle_port_conflict(port, owner)

    # Probe without SO_REUSEADDR: on Windows that flag would let this bind
    # succeed even when another process is already listening on the port,
    # which would mask the conflict.
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
        try:
            sock.bind((host, port))
        except OSError:
            handle_port_conflict(port, owner)

YELLOW = '\033[93m'
RESET = '\033[0m'

def _enable_windows_ansi():
    """Enable ANSI escape sequences in Windows console (no-op elsewhere)."""
    if os.name != 'nt':
        return
    try:
        import ctypes
        kernel32 = ctypes.windll.kernel32
        handle = kernel32.GetStdHandle(-11)  # STD_OUTPUT_HANDLE
        mode = ctypes.c_uint32()
        if kernel32.GetConsoleMode(handle, ctypes.byref(mode)):
            kernel32.SetConsoleMode(handle, mode.value | 0x0004)  # ENABLE_VIRTUAL_TERMINAL_PROCESSING
    except Exception:
        pass

def handle_port_conflict(port, owner):
    """Offer to stop the process holding the port; exit if the user declines."""
    _enable_windows_ansi()
    print()
    print(f"{YELLOW}Port {port} is already in use.{RESET}")
    if owner:
        if owner.get('Name'):
            print(f"  Process: {owner.get('Name')} (PID {owner.get('Pid')})")
        else:
            print(f"  PID: {owner.get('Pid')}")
        if owner.get('Path'):
            print(f"  Path: {owner.get('Path')}")
    print()
    print("The MyGameBuilder local SWF expects the backend on http://127.0.0.1:3000.")

    pid = owner.get('Pid') if owner else None
    if pid is None:
        print("Could not identify the owning process. Stop it manually and try again.")
        sys.exit(1)

    try:
        answer = input(f"{YELLOW}Stop PID {pid} and continue? [y/N]: {RESET}").strip().lower()
    except EOFError:
        answer = ''
    if answer not in ('y', 'yes'):
        print("Aborted by user.")
        sys.exit(1)

    if not stop_process(pid):
        print(f"ERROR: Failed to stop PID {pid}. Try running as Administrator.")
        sys.exit(1)

    # Wait briefly for the OS to release the port.
    for _ in range(20):
        if describe_port_owner(port) is None:
            print(f"Port {port} is now free.")
            return
        time.sleep(0.1)
    print(f"ERROR: PID {pid} was signalled but port {port} is still in use.")
    sys.exit(1)

def stop_process(pid):
    """Forcefully terminate a process by PID. Returns True on success."""
    try:
        if os.name == 'nt':
            result = subprocess.run(
                ['taskkill', '/PID', str(pid), '/F'],
                capture_output=True, text=True, timeout=10,
            )
            return result.returncode == 0
        else:
            os.kill(int(pid), signal.SIGTERM)
            for _ in range(20):
                try:
                    os.kill(int(pid), 0)
                except ProcessLookupError:
                    return True
                time.sleep(0.1)
            os.kill(int(pid), signal.SIGKILL)
            return True
    except Exception as exc:
        print(f"  {exc}")
        return False

def soap_envelope(body_content):
    """Wrap SOAP body content in proper envelope with soapenv prefix."""
    return f'''<?xml version="1.0" encoding="UTF-8"?>
<soapenv:Envelope xmlns:soapenv="{SOAP_ENV_NS}" 
                  xmlns:SOAP-ENC="{SOAP_ENC_NS}"
                  xmlns:xsi="{XSI_NS}" 
                  xmlns:xsd="{XSD_NS}" 
                  soapenv:encodingStyle="{SOAP_ENC_NS}">
  <soapenv:Body>
{body_content}
  </soapenv:Body>
</soapenv:Envelope>'''

def parse_soap_request(xml_data):
    """Parse SOAP request and extract operation and parameters."""
    try:
        root = ET.fromstring(xml_data)
        # Find Body element (with or without namespace)
        body = root.find('.//{http://schemas.xmlsoap.org/soap/envelope/}Body')
        if body is None:
            body = root.find('.//Body')

        # Extract first child of Body (the operation)
        operation_elem = list(body)[0] if body is not None else None
        if operation_elem is None:
            return None, {}

        # Extract operation name (strip namespace)
        operation = operation_elem.tag.split('}')[-1] if '}' in operation_elem.tag else operation_elem.tag

        # Extract parameters
        params = {}
        for child in operation_elem:
            tag = local_name(child)
            params[tag] = child.text or ''

        return operation, params
    except Exception as e:
        print(f"[SOAP] Parse error: {type(e).__name__}: {e}; request_bytes={len(xml_data or '')}")
        return None, {}

def extract_metadata_from_soap(xml_data):
    """Extract nested S3 Metadata Name/Value pairs from a SOAP request."""
    metadata = {}
    root = ET.fromstring(xml_data)
    for meta_elem in root.iter():
        if local_name(meta_elem) != 'Metadata':
            continue

        name = None
        value = None
        for child in meta_elem:
            child_tag = local_name(child)
            if child_tag == 'Name':
                name = child.text or ''
            elif child_tag == 'Value':
                value = child.text or ''

        if name:
            metadata[name] = value or ''

    return metadata

def game_stat_row(username, gamename):
    conn = get_db()
    c = conn.cursor()
    c.execute('''
        INSERT OR IGNORE INTO game_stats (username, gamename)
        VALUES (?, ?)
    ''', (username, gamename))
    conn.commit()
    c.execute('SELECT * FROM game_stats WHERE username = ? AND gamename = ?', (username, gamename))
    row = c.fetchone()
    conn.close()
    return row

def game_stat_xml(row):
    graphics_count = int(row['rating_graphics_count'] or 0)
    gameplay_count = int(row['rating_gameplay_count'] or 0)
    graphics_average = (float(row['rating_graphics_total'] or 0) / graphics_count) if graphics_count else 0
    gameplay_average = (float(row['rating_gameplay_total'] or 0) / gameplay_count) if gameplay_count else 0
    return f'''<gamestat>
  <user>{xml_escape(row['username'])}</user>
  <game>{xml_escape(row['gamename'])}</game>
  <plays-counter>{int(row['plays_counter'] or 0)}</plays-counter>
  <completions-counter>{int(row['completions_counter'] or 0)}</completions-counter>
  <rating_average_graphics>{graphics_average:.2f}</rating_average_graphics>
  <rating_count_graphics>{graphics_count}</rating_count_graphics>
  <rating_average_gameplay>{gameplay_average:.2f}</rating_average_gameplay>
  <rating_count_gameplay>{gameplay_count}</rating_count_gameplay>
  <gamestatus>{int(row['gamestatus'] or 0)}</gamestatus>
  <gametype>{int(row['gametype'] or 0)}</gametype>
  <gamegenre>{xml_escape(row['gamegenre'] or '')}</gamegenre>
  <description>{xml_escape(row['description'] or '')}</description>
</gamestat>'''

def game_ratings_xml(username, gamename, ratername=''):
    row = game_stat_row(username, gamename)
    graphics_count = int(row['rating_graphics_count'] or 0)
    gameplay_count = int(row['rating_gameplay_count'] or 0)
    graphics_average = (float(row['rating_graphics_total'] or 0) / graphics_count) if graphics_count else 0
    gameplay_average = (float(row['rating_gameplay_total'] or 0) / gameplay_count) if gameplay_count else 0
    return xml_fragment(
        f'<user>{xml_escape(username)}</user>',
        f'<game>{xml_escape(gamename)}</game>',
        f'<grme>{xml_escape(ratername)}</grme>',
        f'<gpme>{xml_escape(ratername)}</gpme>',
        f'<graphics_average>{graphics_average:.2f}</graphics_average>',
        f'<graphics_count>{graphics_count}</graphics_count>',
        f'<gameplay_average>{gameplay_average:.2f}</gameplay_average>',
        f'<gameplay_count>{gameplay_count}</gameplay_count>',
    )

# Initialize database on startup
init_db()

def print_banner():
    print(f"""
╔══════════════════════════════════════════════════════════════╗
║  MyGameBuilder Local Backend                                 ║
║  Flask + SQLite + overlay archive                            ║
║  Listening on http://127.0.0.1:3000                          ║
║                                                              ║
║  Seeded accounts:                                            ║
║    foo / bar        (recommended for local editing)         ║
║    guest / guest    (read-only in Flash client)             ║
║    !system / system (system tutorials/badges)               ║
║                                                              ║
║  Archive (read-only S3 snapshot):                            ║
║    {str(ARCHIVE_ROOT):<58}║
║  Data (writable overlay; all saves land here):               ║
║    {str(DATA_ROOT):<58}║
║  Archived users can sign in with ANY password.               ║
╚══════════════════════════════════════════════════════════════╝
""")

# ===== Basic routes =====

# Per-run token used by the launcher scripts to confirm THIS backend
# instance is the one responding (not a stale server on the same port).
LAUNCH_TOKEN = os.environ.get('MGB_LAUNCH_TOKEN', '')

@app.route("/healthz")
def healthz():
    return Response(LAUNCH_TOKEN or 'ok', mimetype='text/plain')

@app.route("/")
def index():
    return '''<h1>MyGameBuilder Local Backend</h1>
<p>Flask + SQLite backend for legacy Flash client.</p>
<p>Accounts: <code>foo/bar</code>, <code>guest/guest</code></p>
<p>Piece storage persistent in <code>server/mgb_local.db</code></p>
<p><a href="/play">Launch MyGameBuilder in the browser with Ruffle</a></p>'''

@app.route("/play")
def play():
    """Browser runner for the SWF using Ruffle."""
    html = rf'''<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>MyGameBuilder Local</title>
  <style>
    html, body {{ height: 100%; margin: 0; background: #1e1e1e; color: #eee; font-family: Arial, sans-serif; }}
    body {{ display: flex; align-items: center; justify-content: center; }}
    main {{ width: min(100vw, 1366px); height: min(100vh, 768px); display: flex; align-items: center; justify-content: center; }}
    ruffle-player {{ display: block; width: 100%; height: 100%; max-width: 1366px; max-height: 768px; }}
    a {{ color: #8ec5ff; }}
  </style>
</head>
<body>
  <main id="player"></main>
  <script>
    window.RufflePlayer = window.RufflePlayer || {{}};
    window.RufflePlayer.config = {{
      autoplay: 'on',
      unmuteOverlay: 'hidden',
      splashScreen: false,
      allowScriptAccess: true,
      socketProxy: [],
      upgradeToHttps: false,
      urlRewriteRules: [
        ['https://s3.amazonaws.com/soap', '/soap'],
        ['http://s3.amazonaws.com/soap', '/soap'],
        ['http://50.18.54.95:3000/user/flexlogin', '/user/flexlogin'],
        ['http://50.18.54.95:3000/user/flexcreateuser', '/user/flexcreateuser'],
        ['http://50.18.54.95:3000/user/flexlogout', '/user/flexlogout'],
        ['http://50.18.54.95:3000/user/flex_heartbeat_safe', '/user/flex_heartbeat_safe'],
        ['http://50.18.54.95:3000/user/get_user_stats', '/user/get_user_stats'],
        ['http://50.18.54.95:3000/user/flex_browse_users', '/user/flex_browse_users'],
        ['http://50.18.54.95:3000/log/logbug', '/log/logbug'],
        [/^http:\/\/50\.18\.54\.95:3000\/(.*)$/i, '/$1'],
        [/^http:\/\/s3\.amazonaws\.com\/apphost\/storage\/(.*)$/i, '/$1'],
        [/^http:\/\/s3\.amazonaws\.com\/apphost\/(.*)$/i, '/$1'],
        [/^https:\/\/s3\.amazonaws\.com\/apphost\/storage\/(.*)$/i, '/$1'],
        [/^https:\/\/s3\.amazonaws\.com\/apphost\/(.*)$/i, '/$1'],
      ],
    }};
  </script>
  <script src="https://unpkg.com/@ruffle-rs/ruffle"></script>
  <script>
    const ruffle = window.RufflePlayer.newest();
    const player = ruffle.createPlayer();
    document.getElementById('player').appendChild(player);
    player.load({{ url: '/{SWF_NAME}' }});
  </script>
</body>
</html>'''
    return Response(html, mimetype='text/html')

@app.route(f"/{SWF_NAME}")
def swf_file():
    """Serve the Flash client from the same origin as the backend."""
    swf_path = CLIENT_DIR / SWF_NAME
    if not swf_path.exists():
        print(f"[ASSET] Missing {SWF_NAME} at {swf_path}")
        return Response(
            f"{SWF_NAME} is missing from {CLIENT_DIR}. "
            "Place the Flash client there and reload.",
            status=404,
            mimetype='text/plain',
        )
    return send_from_directory(CLIENT_DIR, SWF_NAME, mimetype='application/x-shockwave-flash')

@app.route("/archive/<path:filename>")
def archive_file(filename):
    """Serve legacy archive assets expected by the Flash client from client/storage/."""
    return send_storage_file(CLIENT_DIR / 'storage', filename)

@app.route("/carousel_images/<path:filename>")
@app.route("/mascot_images/<path:filename>")
@app.route("/game_music/<path:filename>")
def storage_asset_alias(filename):
    """Serve apphost/storage URL rewrites without the /archive prefix."""
    category = request.path.strip('/').split('/', 1)[0]
    return send_storage_file(CLIENT_DIR / 'storage' / category, filename)

def resolve_case_insensitive_file(base_dir, filename):
    """Find a file under base_dir using case-insensitive path matching."""
    current = Path(base_dir)
    for part in Path(filename).parts:
        if part in ('', '.', '..'):
            return None
        candidate = current / part
        if candidate.exists():
            current = candidate
            continue

        if not current.is_dir():
            return None

        matches = [child for child in current.iterdir() if child.name.lower() == part.lower()]
        if not matches:
            return None
        current = matches[0]

    return current if current.is_file() else None

def send_storage_file(base_dir, filename):
    """Serve storage assets with case-insensitive lookup and mascot fallbacks."""
    resolved = resolve_case_insensitive_file(base_dir, filename)
    if resolved is None and Path(base_dir).name == 'mascot_images':
        fallback_name = {
            'mascotkronz.png': 'MascotRpgGuy.png',
            'mascotdusk.png': 'MascotSchmoopV2.png',
            'mascotguy.png': 'MascotRpgGuy.png',
        }.get(filename.lower())
        if fallback_name:
            resolved = resolve_case_insensitive_file(base_dir, fallback_name)

    if resolved is None:
        print(f"[ASSET] Missing storage asset: {Path(base_dir).name}/{filename}")
        return Response('Not found', status=404)

    response = send_from_directory(resolved.parent, resolved.name)
    response.cache_control.no_cache = True
    response.cache_control.max_age = 0
    return response

@app.route("/crossdomain.xml")
def crossdomain():
    """Flash crossdomain policy - allow all for local development."""
    xml = '''<?xml version="1.0"?>
<cross-domain-policy>
  <allow-access-from domain="*" />
</cross-domain-policy>'''
    return Response(xml, mimetype='text/xml')

# ===== Auth and account endpoints =====

@app.route("/user/flexlogin", methods=["POST"])
def flexlogin():
    """
    Flash login endpoint. Returns XML fragment for Flex HTTPService resultFormat=object.
    Flex reads result.status, result.message, result.logincount.
    """
    login = request.form.get('login', '').strip()
    password = request.form.get('password', '').strip()

    # Compatibility: Ruffle sometimes sends empty form even when UI shows text
    # Treat empty login as guest for local compatibility
    if not login:
        login = 'guest'
        password = 'guest'
        print(f"[LOGIN] Empty form body, treating as guest login")

    print(f"[LOGIN] Attempt: login='{login}', passwordLength={len(password)}")

    # Archive-backed login: real MyGameBuilder password hashes were never
    # part of the public S3 bucket, so we can't validate credentials
    # against the archive. Policy: any user that exists *either* in the
    # local SQLite users table (seeded accounts) *or* as a directory in
    # the archive can sign in. SQLite users still get a real password
    # check; archive-only users bypass the password gate.
    conn = get_db()
    c = conn.cursor()
    c.execute('SELECT * FROM users WHERE login = ?', (login,))
    user = c.fetchone()

    archived = archive.user_exists(login)

    if user is not None:
        # Rows with an empty password are "archive ghosts" auto-created on
        # a previous archive-backed sign-in. Treat them like archive logins
        # and bypass the password check; otherwise an archive user with no
        # known password could never log in again after their first visit.
        stored_password = user['password'] or ''
        is_ghost = stored_password == '' and archived
        if not is_ghost and stored_password != password:
            conn.close()
            print(f"[LOGIN] Failed (bad password): {login}")
            return Response(
                xml_fragment(
                    '<status>0</status>',
                    '<message>Invalid username or password</message>',
                    '<logincount>0</logincount>'
                ),
                mimetype='text/xml'
            )
        new_count = user['login_count'] + 1
        c.execute('UPDATE users SET login_count = ?, updated_at = CURRENT_TIMESTAMP WHERE login = ?',
                  (new_count, login))
        conn.commit()
        conn.close()
        source = 'archive-ghost' if is_ghost else 'db'
        print(f"[LOGIN] Success ({source}): {login} (login #{new_count})")
        return Response(
            xml_fragment(
                '<status>1</status>',
                f'<message>Welcome back, {login}!</message>',
                f'<logincount>{new_count}</logincount>'
            ),
            mimetype='text/xml'
        )

    if archived:
        # Auto-create a local row for the archived user so subsequent
        # logins, browse, and stats see them. No password is stored.
        c.execute('''
            INSERT INTO users (login, password, email, secret_question, secret_answer, dob, login_count)
            VALUES (?, '', ?, 'Archived user?', 'Yes', '01/01/2000', 1)
        ''', (login, f'{login}@archive.local'))
        conn.commit()
        conn.close()
        print(f"[LOGIN] Success (archive, no password required): {login}")
        return Response(
            xml_fragment(
                '<status>1</status>',
                f'<message>Welcome back, {login}!</message>',
                '<logincount>1</logincount>'
            ),
            mimetype='text/xml'
        )

    conn.close()
    print(f"[LOGIN] Failed (no such user): {login}")
    return Response(
        xml_fragment(
            '<status>0</status>',
            '<message>Invalid username or password</message>',
            '<logincount>0</logincount>'
        ),
        mimetype='text/xml'
    )

@app.route("/user/flexcreateuser", methods=["POST"])
def flexcreateuser():
    """Create new user account."""
    login = request.form.get('login', '').strip()
    password = request.form.get('password', '').strip()
    email = request.form.get('email', '').strip()
    dob = request.form.get('dob', '01/01/2000').strip()
    secret_question = request.form.get('secretquestion', 'Default?').strip()
    secret_answer = request.form.get('secretanswer', 'Yes').strip()

    print(f"[CREATE] Attempt: login='{login}', email='{email}'")

    if not login or not password:
        return Response(
            xml_fragment('<status>0</status>', '<message>Username and password required</message>'),
            mimetype='text/xml'
        )

    conn = get_db()
    c = conn.cursor()
    c.execute('SELECT login FROM users WHERE login = ?', (login,))
    if c.fetchone():
        conn.close()
        print(f"[CREATE] Failed: {login} already exists")
        return Response(
            xml_fragment('<status>0</status>', '<message>Username already taken</message>'),
            mimetype='text/xml'
        )

    c.execute('''
        INSERT INTO users (login, password, email, secret_question, secret_answer, dob, login_count)
        VALUES (?, ?, ?, ?, ?, ?, 1)
    ''', (login, password, email, secret_question, secret_answer, dob))
    conn.commit()
    conn.close()

    print(f"[CREATE] Success: {login}")
    return Response(
        xml_fragment(
            '<status>1</status>',
            f'<message>Account created for {login}</message>',
            '<logincount>1</logincount>'
        ),
        mimetype='text/xml'
    )

@app.route("/user/flexlogout", methods=["POST"])
def flexlogout():
    """Logout endpoint - just return success."""
    print("[LOGOUT] User logged out")
    return Response(
        xml_fragment('<status>1</status>', '<message>Logged out</message>'),
        mimetype='text/xml'
    )

@app.route("/user/flex_heartbeat_safe", methods=["GET", "POST"])
def flex_heartbeat_safe():
    """
    Heartbeat endpoint - returns S3 SOAP signature keys and datetime.
    SSSSession.as expects result.keyz (ampersand-separated) and result.dt.
    For local use, we don't validate signatures, just return dummy data.
    """
    authenticateduser = request.form.get('authenticateduser', '')
    clientversion = request.form.get('clientversion', '')

    print(f"[HEARTBEAT] user={authenticateduser}, version={clientversion}")

    # Return dummy signature keys (4 operations: PutObjectInline, GetObject, ListBucket, DeleteObject)
    # Flash client will use these but we won't validate them locally
    fake_keys = 'DUMMYKEY1&DUMMYKEY2&DUMMYKEY3&DUMMYKEY4'
    dt = soap_datetime_now()

    return Response(
        xml_fragment(
            f'<keyz>{fake_keys}</keyz>',
            f'<dt>{dt}</dt>',
            '<status>ok</status>'
        ),
        mimetype='text/xml'
    )

@app.route("/user/get_user_stats", methods=["GET", "POST"])
def get_user_stats():
    """Return user stats like quota info."""
    username = request.form.get('username', 'guest')

    used_kb = archive.user_size_bytes(username) // 1024
    max_kb = 16384  # 16 MB default quota

    print(f"[STATS] {username}: {used_kb} KB / {max_kb} KB")

    return Response(
        xml_fragment(
            '<status>1</status>',
            f'<usedKB>{used_kb}</usedKB>',
            f'<maxKB>{max_kb}</maxKB>'
        ),
        mimetype='text/xml'
    )

@app.route("/user/flex_browse_users", methods=["GET", "POST"])
def flex_browse_users():
    """Return list of users for browse dialog.

    Combines users from the SQLite ``users`` table and every top-level
    directory in the archive (deduped by login)."""
    conn = get_db()
    c = conn.cursor()
    c.execute('SELECT login, login_count FROM users')
    db_users = {row['login']: row['login_count'] for row in c.fetchall()}
    conn.close()

    for archived in archive.list_users():
        db_users.setdefault(archived, 0)

    # Sort by login count desc, then login asc; cap to 20 like before.
    ordered = sorted(db_users.items(), key=lambda kv: (-kv[1], kv[0]))[:20]

    user_elements = []
    for login, login_count in ordered:
        user_elements.append(
            f'<user><login>{xml_escape(login)}</login><logincount>{login_count}</logincount></user>'
        )

    if user_elements:
        users_xml = '<users>' + ''.join(user_elements) + '</users>'
    else:
        users_xml = '<users></users>'

    return Response(
        xml_fragment('<status>1</status>', users_xml),
        mimetype='text/xml'
    )

# Password recovery stubs
@app.route("/user/flexrecoveryquestionrequest", methods=["POST"])
def flexrecoveryquestionrequest():
    """Return security question for password recovery."""
    login = request.form.get('login', '')

    conn = get_db()
    c = conn.cursor()
    c.execute('SELECT secret_question FROM users WHERE login = ?', (login,))
    user = c.fetchone()
    conn.close()

    if user:
        return Response(
            xml_fragment(
                '<status>1</status>',
                f'<question>{user["secret_question"]}</question>'
            ),
            mimetype='text/xml'
        )
    else:
        return Response(
            xml_fragment('<status>0</status>', '<message>User not found</message>'),
            mimetype='text/xml'
        )

@app.route("/user/flexrecoverpassword", methods=["POST"])
def flexrecoverpassword():
    """Recover password using security answer."""
    login = request.form.get('login', '')
    answer = request.form.get('answer', '')

    conn = get_db()
    c = conn.cursor()
    c.execute('SELECT password, secret_answer FROM users WHERE login = ?', (login,))
    user = c.fetchone()
    conn.close()

    if user and user['secret_answer'].lower() == answer.lower():
        return Response(
            xml_fragment(
                '<status>1</status>',
                f'<password>{user["password"]}</password>',
                '<message>Password recovered</message>'
            ),
            mimetype='text/xml'
        )
    else:
        return Response(
            xml_fragment('<status>0</status>', '<message>Incorrect answer</message>'),
            mimetype='text/xml'
        )

@app.route("/user/flexchangepassword", methods=["POST"])
def flexchangepassword():
    """Change user password."""
    login = request.form.get('login', '')
    oldpassword = request.form.get('oldpassword', '')
    newpassword = request.form.get('newpassword', '')

    conn = get_db()
    c = conn.cursor()
    c.execute('SELECT password FROM users WHERE login = ?', (login,))
    user = c.fetchone()

    if user and user['password'] == oldpassword:
        c.execute('UPDATE users SET password = ?, updated_at = CURRENT_TIMESTAMP WHERE login = ?',
                  (newpassword, login))
        conn.commit()
        conn.close()
        print(f"[PASSWORD] Changed for {login}")
        return Response(
            xml_fragment('<status>1</status>', '<message>Password changed</message>'),
            mimetype='text/xml'
        )
    else:
        conn.close()
        return Response(
            xml_fragment('<status>0</status>', '<message>Invalid old password</message>'),
            mimetype='text/xml'
        )

# Debug/logging endpoint
@app.route("/log/logbug", methods=["GET", "POST"])
def logbug():
    """Log client errors/debug info."""
    message = request.form.get('message', '')
    print(f"[CLIENT LOG] {message}")
    return Response('<status>ok</status>', mimetype='text/xml')

# ===== S3 SOAP emulation =====

@app.route("/soap", methods=["POST"])
@app.route("/s3soap", methods=["POST"])
@app.route("/apphost/soap", methods=["POST"])
def s3soap():
    """
    S3 SOAP endpoint for piece storage (tiles, actors, maps, profiles, tutorials, screenshots).
    Supports: PutObjectInline, GetObject, ListBucket, DeleteObject.
    """
    xml_data = request.data.decode('utf-8')
    operation, params = parse_soap_request(xml_data)

    if not operation:
        print("[SOAP] Could not parse request")
        return Response(soap_envelope('<soapenv:Fault><faultstring>Invalid request</faultstring></soapenv:Fault>'),
                       mimetype='text/xml')

    log_soap_summary(operation, params.get('Key', ''), params.get('ContentLength'))

    if operation == 'PutObjectInline':
        return soap_put_object_inline(params)
    elif operation == 'GetObject':
        return soap_get_object(params)
    elif operation == 'ListBucket':
        return soap_list_bucket(params)
    elif operation == 'DeleteObject':
        return soap_delete_object(params)
    else:
        print(f"[SOAP] Unsupported operation: {operation}")
        return Response(soap_envelope(f'<soapenv:Fault><faultstring>Unsupported operation: {operation}</faultstring></soapenv:Fault>'),
                       mimetype='text/xml')

def soap_put_object_inline(params):
    """
    Store a piece (tile, actor, map, profile, tutorial, screenshot) into
    the filesystem archive. Bytes are decoded from base64 before being
    written so the on-disk layout matches the archive's natural format.
    """
    bucket = params.get('Bucket', ARCHIVE_BUCKET)
    key = params.get('Key', '')
    data = params.get('Data', '')
    content_length = int(params.get('ContentLength', len(data)))

    try:
        metadata = extract_metadata_from_soap(request.data.decode('utf-8'))
    except Exception as e:
        metadata = {}
        print(f"[SOAP PUT] Metadata parse error for {key}: {type(e).__name__}: {e}")

    log_soap_summary('PutObjectInline', key, content_length, metadata)

    try:
        body = base64.b64decode(data) if data else b''
    except Exception as exc:
        print(f"[SOAP PUT] Base64 decode failed for {key}: {exc}")
        body = b''

    # Write to the archive (creates collision .map.json files as needed).
    try:
        archive.put(key, body, content_type=metadata.get('Content-Type'),
                    amz_meta=metadata)
    except Exception as exc:
        print(f"[SOAP PUT] Archive write failed for {key}: {exc}")
        return Response(soap_envelope(
            f'<soapenv:Fault><faultstring>Archive write failed: {xml_escape(str(exc))}</faultstring></soapenv:Fault>'
        ), mimetype='text/xml', status=500)

    # Return success response
    response_body = f'''    <ns1:PutObjectInlineResponse xmlns:ns1="{AWS_NS}">
      <ns1:PutObjectInlineResponse>
        <ns1:Timestamp>{soap_datetime_now()}</ns1:Timestamp>
      </ns1:PutObjectInlineResponse>
    </ns1:PutObjectInlineResponse>'''

    return Response(soap_envelope(response_body), mimetype='text/xml')

def soap_get_object(params):
    """
    Load a piece by key. Reads from the overlay archive (``data/`` then
    ``archive/``); 404s if neither layer has it.
    """
    key = params.get('Key', '')

    log_soap_summary('GetObject', key)

    obj = archive.get(key)
    if obj is None:
        print(f"[SOAP GET] Not found: {key}")
        fault = f'''    <soapenv:Fault>
      <faultcode>Client.NoSuchKey</faultcode>
      <faultstring>The specified key does not exist</faultstring>
    </soapenv:Fault>'''
        return Response(soap_envelope(fault), mimetype='text/xml', status=404)

    data_base64 = obj.read_base64()
    metadata = dict(obj.amz_meta)
    if obj.content_type and 'Content-Type' not in metadata:
        metadata['Content-Type'] = obj.content_type
    last_modified = obj.last_modified

    # Build metadata XML elements
    metadata_xml = []
    for name, value in metadata.items():
        metadata_xml.append(f'''      <ns1:Metadata>
        <ns1:Name>{xml_escape(name)}</ns1:Name>
        <ns1:Value>{xml_escape(value)}</ns1:Value>
      </ns1:Metadata>''')

    response_body = f'''    <ns1:GetObjectResponse xmlns:ns1="{AWS_NS}">
      <ns1:GetObjectResponse>
        <ns1:Data>{xml_escape(data_base64)}</ns1:Data>
{''.join(metadata_xml)}
        <ns1:LastModified>{xml_escape(last_modified)}</ns1:LastModified>
      </ns1:GetObjectResponse>
    </ns1:GetObjectResponse>'''

    return Response(soap_envelope(response_body), mimetype='text/xml')

def soap_list_bucket(params):
    """
    List pieces by prefix (for PieceList and ProjectList).
    Sourced entirely from the overlay archive (``data/`` overlaid on
    ``archive/``); tombstoned entries are excluded.
    """
    bucket = params.get('Bucket', ARCHIVE_BUCKET)
    prefix = params.get('Prefix', '')
    marker = params.get('Marker', '')
    max_keys = int(params.get('MaxKeys', 1000))
    delimiter = params.get('Delimiter', '')

    log_soap_summary('ListBucket', extra=f"Prefix: {prefix}, Marker: {marker}, MaxKeys: {max_keys}, Delimiter: {delimiter}")

    items = {}  # key -> (size, last_modified)
    for obj in archive.iter_keys_with_prefix(prefix):
        items[obj.key] = (obj.size, obj.last_modified)

    # Apply marker + sort + max_keys (+1 to detect truncation).
    keys_sorted = sorted(items.keys())
    if marker:
        keys_sorted = [k for k in keys_sorted if k > marker]
    is_truncated = len(keys_sorted) > max_keys
    if is_truncated:
        keys_sorted = keys_sorted[:max_keys]

    # Build Contents XML
    contents_xml = []

    if delimiter:
        # Group by common prefixes (for project listing).
        prefixes_seen = set()
        for key in keys_sorted:
            remainder = key[len(prefix):]
            delim_pos = remainder.find(delimiter)
            if delim_pos > 0:
                common_prefix = prefix + remainder[:delim_pos + 1]
                if common_prefix not in prefixes_seen:
                    prefixes_seen.add(common_prefix)
                    contents_xml.append(f'''      <ns1:CommonPrefixes>
        <ns1:Prefix>{xml_escape(common_prefix)}</ns1:Prefix>
      </ns1:CommonPrefixes>''')
            else:
                size, last_modified = items[key]
                contents_xml.append(f'''      <ns1:Contents>
        <ns1:Key>{xml_escape(key)}</ns1:Key>
        <ns1:LastModified>{xml_escape(last_modified)}</ns1:LastModified>
        <ns1:Size>{size}</ns1:Size>
      </ns1:Contents>''')
    else:
        for key in keys_sorted:
            size, last_modified = items[key]
            contents_xml.append(f'''      <ns1:Contents>
        <ns1:Key>{xml_escape(key)}</ns1:Key>
        <ns1:LastModified>{xml_escape(last_modified)}</ns1:LastModified>
        <ns1:Size>{size}</ns1:Size>
      </ns1:Contents>''')

    response_body = f'''    <ns1:ListBucketResponse xmlns:ns1="{AWS_NS}">
      <ns1:ListBucketResponse>
        <ns1:Name>{xml_escape(bucket)}</ns1:Name>
        <ns1:Prefix>{xml_escape(prefix)}</ns1:Prefix>
        <ns1:Marker>{xml_escape(marker)}</ns1:Marker>
        <ns1:MaxKeys>{max_keys}</ns1:MaxKeys>
        <ns1:IsTruncated>{"true" if is_truncated else "false"}</ns1:IsTruncated>
{''.join(contents_xml)}
      </ns1:ListBucketResponse>
    </ns1:ListBucketResponse>'''

    print(f"[SOAP LIST] Found {len(keys_sorted)} objects (overlay), truncated={is_truncated}")
    return Response(soap_envelope(response_body), mimetype='text/xml')

def soap_delete_object(params):
    """
    Delete a piece by key. Drops the overlay copy and (if the key lives
    only in the read-only archive) writes a tombstone in ``data/``.
    """
    key = params.get('Key', '')

    log_soap_summary('DeleteObject', key)

    deleted = archive.delete(key)
    code = '204' if deleted else '404'

    response_body = f'''    <ns1:DeleteObjectResponse xmlns:ns1="{AWS_NS}">
      <ns1:DeleteObjectResponse>
        <ns1:Code>{code}</ns1:Code>
      </ns1:DeleteObjectResponse>
    </ns1:DeleteObjectResponse>'''

    return Response(soap_envelope(response_body), mimetype='text/xml')

# Alternative delete endpoint (Rails-style)
@app.route("/user/flex_delete_s3object", methods=["POST"])
def flex_delete_s3object():
    """Rails-style delete endpoint as alternative to SOAP DeleteObject."""
    itemname = request.form.get('itemname', '')

    print(f"[DELETE] Item: {itemname}")

    deleted = archive.delete(itemname)

    if deleted:
        return Response(
            xml_fragment('<mgb_error>0</mgb_error>', '<message>Deleted</message>'),
            mimetype='text/xml'
        )
    else:
        return Response(
            xml_fragment('<mgb_error>404</mgb_error>', '<mgb_error_msg>Not found</mgb_error_msg>'),
            mimetype='text/xml'
        )

# ===== Social/messaging stub endpoints (prevent 404s) =====

@app.route("/user/flex_get_conversations", methods=["GET", "POST"])
def flex_get_conversations():
    """Return empty conversations list."""
    return Response('<conversations></conversations>', mimetype='text/xml')

@app.route("/user/flex_get_message_thread", methods=["GET", "POST"])
def flex_get_message_thread():
    """Return empty message thread."""
    return Response('<messages></messages>', mimetype='text/xml')

@app.route("/user/flex_delete_message_thread", methods=["GET", "POST"])
def flex_delete_message_thread():
    """Acknowledge message thread deletion."""
    return Response('<status>1</status>', mimetype='text/xml')

@app.route("/user/flex_send_message", methods=["GET", "POST"])
def flex_send_message():
    """Acknowledge message sent."""
    print(f"[MESSAGE] {request.form.get('recipient', 'unknown')}")
    return Response('<status>1</status>', mimetype='text/xml')

@app.route("/user/flex_list_friendships", methods=["GET", "POST"])
def flex_list_friendships():
    """Return empty friends list."""
    return Response('<friendships></friendships>', mimetype='text/xml')

@app.route("/user/flex_add_friendship", methods=["GET", "POST"])
def flex_add_friendship():
    """Acknowledge friend request."""
    return Response('<status>1</status>', mimetype='text/xml')

@app.route("/user/flex_delete_friendship", methods=["GET", "POST"])
def flex_delete_friendship():
    """Acknowledge friend removal."""
    return Response('<status>1</status>', mimetype='text/xml')

@app.route("/user/flex_get_wallposts", methods=["GET", "POST"])
def flex_get_wallposts():
    """Return empty wall posts list."""
    return Response('<wallposts></wallposts>', mimetype='text/xml')

@app.route("/user/flex_add_wallpost", methods=["GET", "POST"])
def flex_add_wallpost():
    """Acknowledge wall post."""
    return Response('<status>1</status>', mimetype='text/xml')

@app.route("/user/flex_delete_wallpost", methods=["GET", "POST"])
def flex_delete_wallpost():
    """Acknowledge wall post deletion."""
    return Response('<status>1</status>', mimetype='text/xml')

# Game stats and ratings endpoints
@app.route("/user/flex_get_game_stats", methods=["GET", "POST"])
def flex_get_game_stats():
    """Return local game stats."""
    username = request.form.get('username', request.args.get('username', 'foo'))
    gamename = request.form.get('gamename', request.args.get('gamename', ''))
    row = game_stat_row(username, gamename)
    return Response(game_stat_xml(row), mimetype='text/xml')

@app.route("/user/flex_bump_play_counter", methods=["GET", "POST"])
def flex_bump_play_counter():
    """Get stats and optionally bump local play/completion counters."""
    username = request.form.get('username', request.args.get('username', 'foo'))
    gamename = request.form.get('gamename', request.args.get('gamename', ''))
    bump_plays = int(request.form.get('bumpplayscount', request.args.get('bumpplayscount', 0)) or 0)
    bump_completions = int(request.form.get('bumpcompletionscount', request.args.get('bumpcompletionscount', 0)) or 0)

    row = game_stat_row(username, gamename)
    if bump_plays or bump_completions:
        conn = get_db()
        c = conn.cursor()
        c.execute('''
            UPDATE game_stats
            SET plays_counter = plays_counter + ?,
                completions_counter = completions_counter + ?,
                updated_at = CURRENT_TIMESTAMP
            WHERE username = ? AND gamename = ?
        ''', (bump_plays, bump_completions, username, gamename))
        conn.commit()
        c.execute('SELECT * FROM game_stats WHERE username = ? AND gamename = ?', (username, gamename))
        row = c.fetchone()
        conn.close()

    return Response(game_stat_xml(row), mimetype='text/xml')

@app.route("/user/flex_update_game_metadata", methods=["GET", "POST"])
def flex_update_game_metadata():
    """Persist local game browser metadata."""
    username = request.form.get('username', request.args.get('username', 'foo'))
    gamename = request.form.get('gamename', request.args.get('gamename', ''))
    gamestatus = int(request.form.get('gamestatus', request.args.get('gamestatus', 0)) or 0)
    gametype = int(request.form.get('gametype', request.args.get('gametype', 0)) or 0)
    gamegenre = request.form.get('gamegenre', request.args.get('gamegenre', ''))
    description = request.form.get('description', request.args.get('description', ''))

    game_stat_row(username, gamename)
    conn = get_db()
    c = conn.cursor()
    c.execute('''
        UPDATE game_stats
        SET gamestatus = ?, gametype = ?, gamegenre = ?, description = ?, updated_at = CURRENT_TIMESTAMP
        WHERE username = ? AND gamename = ?
    ''', (gamestatus, gametype, gamegenre, description, username, gamename))
    conn.commit()
    c.execute('SELECT * FROM game_stats WHERE username = ? AND gamename = ?', (username, gamename))
    row = c.fetchone()
    conn.close()
    return Response(game_stat_xml(row), mimetype='text/xml')

@app.route("/user/flex_delete_gamestatus_if_exists", methods=["GET", "POST"])
def flex_delete_gamestatus_if_exists():
    """Delete local game browser metadata if present."""
    username = request.form.get('username', request.args.get('username', 'foo'))
    gamename = request.form.get('gamename', request.args.get('gamename', ''))
    conn = get_db()
    c = conn.cursor()
    c.execute('DELETE FROM game_stats WHERE username = ? AND gamename = ?', (username, gamename))
    conn.commit()
    conn.close()
    row = game_stat_row(username, gamename)
    return Response(game_stat_xml(row), mimetype='text/xml')

@app.route("/user/flex_record_rating", methods=["GET", "POST"])
@app.route("/user/flex_rate_game", methods=["GET", "POST"])
def flex_record_rating():
    """Record a local graphics/gameplay rating."""
    username = request.form.get('username', request.args.get('username', 'foo'))
    gamename = request.form.get('gamename', request.args.get('gamename', ''))
    graphics = int(request.form.get('graphicsrating', request.args.get('graphicsrating', 0)) or 0)
    gameplay = int(request.form.get('gameplayrating', request.args.get('gameplayrating', 0)) or 0)
    ratername = request.form.get('ratername', request.args.get('ratername', ''))

    game_stat_row(username, gamename)
    conn = get_db()
    c = conn.cursor()
    c.execute('''
        UPDATE game_stats
        SET rating_graphics_total = rating_graphics_total + ?,
            rating_graphics_count = rating_graphics_count + ?,
            rating_gameplay_total = rating_gameplay_total + ?,
            rating_gameplay_count = rating_gameplay_count + ?,
            updated_at = CURRENT_TIMESTAMP
        WHERE username = ? AND gamename = ?
    ''', (graphics, 1 if graphics > 0 else 0, gameplay, 1 if gameplay > 0 else 0, username, gamename))
    conn.commit()
    conn.close()
    return Response(xml_fragment(
        '<status>1</status>',
        '<rating>',
        f'<user>{xml_escape(username)}</user>',
        f'<game>{xml_escape(gamename)}</game>',
        f'<ratername>{xml_escape(ratername)}</ratername>',
        '</rating>',
    ), mimetype='text/xml')

@app.route("/user/flex_get_ratings", methods=["GET", "POST"])
def flex_get_ratings():
    """Return local rating averages."""
    username = request.form.get('username', request.args.get('username', 'foo'))
    gamename = request.form.get('gamename', request.args.get('gamename', ''))
    ratername = request.form.get('ratername', request.args.get('ratername', ''))
    return Response(game_ratings_xml(username, gamename, ratername), mimetype='text/xml')

@app.route("/user/flex_list_games_by5", methods=["GET", "POST"])
def flex_list_games_by5():
    """List local game metadata for the game browser."""
    limit = int(request.form.get('limit', request.args.get('limit', 5)) or 5)
    offset = int(request.form.get('offset', request.args.get('offset', 0)) or 0)
    order = request.form.get('order', request.args.get('order', 'plays'))
    gamestatus = request.form.get('gamestatus', request.args.get('gamestatus', ''))
    gametype = request.form.get('gametype', request.args.get('gametype', ''))

    order_sql = 'updated_at DESC'
    if order in ('plays', 'mostplays', 'plays_counter'):
        order_sql = 'plays_counter DESC, updated_at DESC'
    elif order in ('rating', 'rated'):
        order_sql = '(rating_graphics_total + rating_gameplay_total) DESC, updated_at DESC'

    query = 'SELECT * FROM game_stats WHERE 1=1'
    query_params = []
    if gamestatus not in ('', '-1', 'all'):
        query += ' AND gamestatus = ?'
        query_params.append(int(gamestatus))
    if gametype not in ('', '-1', 'all'):
        query += ' AND gametype = ?'
        query_params.append(int(gametype))

    conn = get_db()
    c = conn.cursor()
    c.execute(f'{query} ORDER BY {order_sql} LIMIT ? OFFSET ?', query_params + [limit, offset])
    rows = c.fetchall()
    c.execute(f'SELECT COUNT(*) FROM ({query})', query_params)
    game_count = c.fetchone()[0]
    conn.close()

    gamestats_xml = ''.join(game_stat_xml(row) for row in rows)
    return Response(xml_fragment(
        f'<resultcount>{len(rows)}</resultcount>',
        f'<gamecount>{game_count}</gamecount>',
        '<gamestats>',
        gamestats_xml,
        '</gamestats>',
    ), mimetype='text/xml')

@app.route("/user/flex_log_play", methods=["GET", "POST"])
def flex_log_play():
    """Acknowledge game play logged."""
    return flex_bump_play_counter()

@app.route("/user/flex_get_highscores", methods=["GET", "POST"])
def flex_get_highscores():
    """Return empty highscores list."""
    return Response('<highscores></highscores>', mimetype='text/xml')

@app.route("/user/flex_submit_highscore", methods=["GET", "POST"])
def flex_submit_highscore():
    """Acknowledge highscore submission."""
    return Response('<status>1</status>', mimetype='text/xml')

# Tutorial badges
@app.route("/user/flex_award_tutorial_badge", methods=["GET", "POST"])
def flex_award_tutorial_badge():
    """Acknowledge tutorial badge award."""
    return Response('<status>1</status>', mimetype='text/xml')

@app.route("/user/flex_get_badges", methods=["GET", "POST"])
def flex_get_badges():
    """Return empty badges list."""
    return Response('<badges></badges>', mimetype='text/xml')

# Photo upload stub
@app.route("/user/uploadUserImageFile", methods=["POST"])
def upload_user_image_file():
    """Acknowledge photo upload."""
    return Response('<status>1</status><url>/placeholder.png</url>', mimetype='text/xml')

if __name__ == '__main__':
    ensure_port_available(HOST, PORT)
    print_banner()
    app.run(host=HOST, port=PORT, debug=True, use_reloader=False)
