# MyGameBuilder Local Backend — Reimplementation Spec (for modern C#)

## 1. Purpose & hosting

`mgb_local_server.py` is a single-process HTTP backend that emulates the original MyGameBuilder Rails + Amazon S3 endpoints so the legacy Flash client (run via Ruffle) works fully offline. A C# port must be **wire-compatible**: identical URLs, request parsing, and response bodies/MIME types. Only the on-disk persistence differs (defer to `README.md`/`server/README.md` for the archive layout and `FORMATS.md` for piece byte formats).

- Bind host `0.0.0.0`, **port `3000`** (the client hard-expects `http://127.0.0.1:3000`).
- Startup must: seed the three default accounts (below) if the accounts store is empty, and make sure the writable data store + read-only archive store are reachable.
- Launcher/bootstrap/updater scripts (`bootstrap.py`, `updater.py`, port-conflict prompts) are **out of scope** for the server itself — reimplement only if you want the same turnkey UX.

### Two persistence stores (logical, not on-disk-compatible)
1. **Accounts/stats DB** — relational; holds `users` and `game_stats`. Original uses SQLite `server/mgb_local.db`.
2. **Piece store** — an S3 emulation with **overlay semantics**: a read-only `archive/` snapshot under a writable `data/` overlay.
   - Reads: overlay first, then base.
   - Writes: always to overlay.
   - Delete of a base-only key: leave base intact, record a **tombstone** keyed by the exact S3 key so reads/lists skip it; a later `put` of that key clears the tombstone.
   - Keys are S3-style paths: `JGI_test1/<user>/<project>/<piecetype>/<name>` (bucket constant = `JGI_test1`). Windows-collision disambiguation (`.map.json`, sanitized segments) is an internal detail — **not** visible in any request/response; just preserve exact original S3 keys round-trip.

## 2. Response conventions (critical)

Two response styles. Get these byte-exact.

**A. Flex "object" fragments** — most `/user/*` and `/log/*` endpoints. MIME `text/xml`. The body is **concatenated sibling XML elements with no XML declaration and no wrapping root element**, e.g.:

```
<status>1</status><message>Welcome back, foo!</message><logincount>3</logincount>
```

Flex `HTTPService resultFormat="object"` parses this. Do not pretty-print or add a root.

**B. SOAP envelopes** — the S3 endpoints. MIME `text/xml`, wrapped:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<soapenv:Envelope xmlns:soapenv="http://schemas.xmlsoap.org/soap/envelope/"
                  xmlns:SOAP-ENC="http://schemas.xmlsoap.org/soap/encoding/"
                  xmlns:xsi="http://www.w3.org/1999/XMLSchema-instance"
                  xmlns:xsd="http://www.w3.org/1999/XMLSchema"
                  soapenv:encodingStyle="http://schemas.xmlsoap.org/soap/encoding/">
  <soapenv:Body>
    <!-- operation response, ns1 = http://s3.amazonaws.com/doc/2006-03-01/ -->
  </soapenv:Body>
</soapenv:Envelope>
```

**XML escaping:** escape `&`, `<`, `>`, `"`, `'` in all text content/values.

**Datetime format (UTC):** `yyyy-MM-ddTHH:mm:ss.000Z` (always `.000` ms, `Z` suffix). Used for SOAP timestamps and object `LastModified` (derive `LastModified` from the stored object's modified time).

Form fields are read from POST form bodies; the `/user/*` stat/game endpoints fall back to query-string params when the form field is absent.

## 3. Static / browser endpoints

| Method | Path | Behavior |
|---|---|---|
| GET | `/healthz` | `text/plain`. Returns the launch-token (env `MGB_LAUNCH_TOKEN`) or `ok`. Lets a launcher confirm it reached *this* instance. |
| GET | `/` | Simple HTML landing page. |
| GET | `/play` | HTML page that loads Ruffle and the SWF. Must include `urlRewriteRules` mapping the client's hard-coded origins to local paths (see §8). |
| GET | `/MGB.swf` | Serve the SWF as `application/x-shockwave-flash`; 404 `text/plain` if absent. |
| GET | `/crossdomain.xml` | `text/xml` Flash policy allowing `domain="*"`. |
| GET | `/archive/<path>` | Serve `client/storage/<path>`, **case-insensitive** path resolution, `Cache-Control: no-cache, max-age=0`. |
| GET | `/carousel_images/<f>`, `/mascot_images/<f>`, `/game_music/<f>` | Serve `client/storage/<category>/<f>` (category = first path segment). For `mascot_images`, apply fallbacks: `mascotkronz.png`/`mascotguy.png`→`MascotRpgGuy.png`, `mascotdusk.png`→`MascotSchmoopV2.png`. 404 if unresolved. |

## 4. Auth & account endpoints (fragment responses, `text/xml`)

Default seeded accounts: `foo`/`bar`, `guest`/`guest`, `!system`/`system`.

### POST `/user/flexlogin` — form: `login`, `password`
Logic:
1. Trim both. If `login` empty → treat as `guest`/`guest`.
2. Lookup user in accounts DB; separately check whether the login exists as an **archive user** (a user directory exists in the piece store).
3. If DB row exists:
   - "Archive ghost" = stored password empty **and** archive user exists → bypass password.
   - Otherwise stored password must equal submitted password; mismatch → failure.
   - On success: increment `login_count`, return success with new count.
4. If no DB row but archive user exists: auto-create a DB row with **empty password** (`login_count=1`), succeed. (This is why any password works for archived users.)
5. Else: failure.

Success body: `<status>1</status><message>Welcome back, {login}!</message><logincount>{n}</logincount>`
Failure body: `<status>0</status><message>Invalid username or password</message><logincount>0</logincount>`

### POST `/user/flexcreateuser` — form: `login`, `password`, `email`, `dob`(def `01/01/2000`), `secretquestion`(def `Default?`), `secretanswer`(def `Yes`)
- Missing login/password → `<status>0</status><message>Username and password required</message>`
- Duplicate login → `<status>0</status><message>Username already taken</message>`
- Else insert (`login_count=1`) → `<status>1</status><message>Account created for {login}</message><logincount>1</logincount>`

### POST `/user/flexlogout`
→ `<status>1</status><message>Logged out</message>`

### GET/POST `/user/flex_heartbeat_safe` — form: `authenticateduser`, `clientversion`
Returns dummy S3 signing keys (the client uses but the server never validates them):
`<keyz>DUMMYKEY1&DUMMYKEY2&DUMMYKEY3&DUMMYKEY4</keyz><dt>{soap-datetime}</dt><status>ok</status>`
(4 `&`-separated keys; `dt` = current UTC in the datetime format.)

### GET/POST `/user/get_user_stats` — form: `username`(def `guest`)
`usedKB` = piece-store bytes owned by the user ÷ 1024 (exclude sidecar/metadata files); `maxKB` = `16384`.
→ `<status>1</status><usedKB>{used}</usedKB><maxKB>16384</maxKB>`

### GET/POST `/user/flex_browse_users`
Union of accounts-DB logins (with their `login_count`) and archive user directories (count 0 if not in DB). Sort by `login_count` desc, then login asc; cap **20**.
→ `<status>1</status><users><user><login>{login}</login><logincount>{n}</logincount></user>…</users>` (`<users></users>` if none).

### POST `/user/flexrecoveryquestionrequest` — form: `login`
Found → `<status>1</status><question>{secret_question}</question>`; else `<status>0</status><message>User not found</message>`.

### POST `/user/flexrecoverpassword` — form: `login`, `answer`
Case-insensitive answer match → `<status>1</status><password>{password}</password><message>Password recovered</message>`; else `<status>0</status><message>Incorrect answer</message>`.

### POST `/user/flexchangepassword` — form: `login`, `oldpassword`, `newpassword`
Old matches → update, `<status>1</status><message>Password changed</message>`; else `<status>0</status><message>Invalid old password</message>`.

### GET/POST `/log/logbug` — form: `message`
Log and return `<status>ok</status>`.

## 5. S3 SOAP piece storage

Routes (all POST, all dispatch identically): `/soap`, `/s3soap`, `/apphost/soap`.

**Request parsing:** parse the SOAP body; the first child of `<Body>` is the operation element; its direct children become a params map keyed by local element name → text. Namespaces may or may not be present — match by **local name**. Supported ops: `PutObjectInline`, `GetObject`, `ListBucket`, `DeleteObject`.

- Unparseable request → envelope with `<soapenv:Fault><faultstring>Invalid request</faultstring></soapenv:Fault>` (HTTP 200).
- Unknown op → Fault `Unsupported operation: {op}` (HTTP 200).

### PutObjectInline — params: `Bucket`(def `JGI_test1`), `Key`, `Data`(base64), `ContentLength`
Metadata is gathered separately: scan **all** `<Metadata>` elements anywhere in the request, each having child `<Name>`/`<Value>`, into a dict (these are the `x-amz-meta-*` fields: `width`, `height`, `tilename`, `blobencoding`, `comment`, `acl`, and possibly `Content-Type`).
Decode `Data` from base64 (on failure store empty body). Store body + all metadata pairs under `Key` in the **overlay** store (clearing any tombstone for that key). `content_type` = the `Content-Type` metadata value if present.
Success body (inside envelope):
```xml
<ns1:PutObjectInlineResponse xmlns:ns1="http://s3.amazonaws.com/doc/2006-03-01/">
  <ns1:PutObjectInlineResponse>
    <ns1:Timestamp>{soap-datetime}</ns1:Timestamp>
  </ns1:PutObjectInlineResponse>
</ns1:PutObjectInlineResponse>
```
Storage failure → HTTP 500 envelope Fault `Archive write failed: …`.

### GetObject — params: `Key`
Resolve via overlay (tombstone hides it). Not found → **HTTP 404** envelope:
```xml
<soapenv:Fault>
  <faultcode>Client.NoSuchKey</faultcode>
  <faultstring>The specified key does not exist</faultstring>
</soapenv:Fault>
```
Found → return base64 body, every stored metadata pair, and `LastModified`. Echo back **all** Name/Value metadata that was stored on PUT, and additionally include a `Content-Type` pair if a content type is known and not already present:
```xml
<ns1:GetObjectResponse xmlns:ns1="http://s3.amazonaws.com/doc/2006-03-01/">
  <ns1:GetObjectResponse>
    <ns1:Data>{base64, xml-escaped}</ns1:Data>
    <ns1:Metadata><ns1:Name>{name}</ns1:Name><ns1:Value>{value}</ns1:Value></ns1:Metadata>
    …repeat per metadata pair…
    <ns1:LastModified>{soap-datetime}</ns1:LastModified>
  </ns1:GetObjectResponse>
</ns1:GetObjectResponse>
```
(The client decides how to parse the body by `piecetype` in the key, not by `Content-Type` — see `FORMATS.md`.)

### ListBucket — params: `Bucket`(def `JGI_test1`), `Prefix`, `Marker`, `MaxKeys`(def 1000), `Delimiter`
Collect every non-tombstoned key (overlay unioned over base, overlay wins) whose **original S3 key** starts with `Prefix`. Sort keys ascending; if `Marker` set, keep only keys strictly `> Marker`. `IsTruncated` = there were more than `MaxKeys`; then trim to `MaxKeys`.
- No delimiter: emit one `<ns1:Contents>` per key with `Key`, `LastModified`, `Size`.
- With delimiter: for the substring of each key after `Prefix`, if the delimiter appears at position > 0, emit a deduped `<ns1:CommonPrefixes><ns1:Prefix>{prefix+leadingpart+delim}</ns1:Prefix></ns1:CommonPrefixes>`; otherwise emit `<ns1:Contents>` as above. (Used for project listing.)

Body:
```xml
<ns1:ListBucketResponse xmlns:ns1="http://s3.amazonaws.com/doc/2006-03-01/">
  <ns1:ListBucketResponse>
    <ns1:Name>{bucket}</ns1:Name>
    <ns1:Prefix>{prefix}</ns1:Prefix>
    <ns1:Marker>{marker}</ns1:Marker>
    <ns1:MaxKeys>{maxKeys}</ns1:MaxKeys>
    <ns1:IsTruncated>{true|false}</ns1:IsTruncated>
    …Contents / CommonPrefixes…
  </ns1:ListBucketResponse>
</ns1:ListBucketResponse>
```

### DeleteObject — params: `Key`
Delete via overlay semantics (remove overlay copy and/or tombstone a base-only key). `Code` = `204` if something was removed/hidden, else `404`:
```xml
<ns1:DeleteObjectResponse xmlns:ns1="http://s3.amazonaws.com/doc/2006-03-01/">
  <ns1:DeleteObjectResponse><ns1:Code>{204|404}</ns1:Code></ns1:DeleteObjectResponse>
</ns1:DeleteObjectResponse>
```

### POST `/user/flex_delete_s3object` — form: `itemname` (Rails-style alternate delete)
Delete that key. Deleted → `<mgb_error>0</mgb_error><message>Deleted</message>`; not found → `<mgb_error>404</mgb_error><mgb_error_msg>Not found</mgb_error_msg>`.

## 6. Game stats & ratings (fragment responses, `text/xml`)

A `game_stats` row is keyed by (`username`, `gamename`) and **auto-created zeroed** on first reference. Reusable `<gamestat>` block (note hyphenated counter names, underscore rating names, averages to **2 decimals**, average = total ÷ count or `0` when count is 0):

```xml
<gamestat>
  <user>{username}</user>
  <game>{gamename}</game>
  <plays-counter>{int}</plays-counter>
  <completions-counter>{int}</completions-counter>
  <rating_average_graphics>{avg:0.00}</rating_average_graphics>
  <rating_count_graphics>{int}</rating_count_graphics>
  <rating_average_gameplay>{avg:0.00}</rating_average_gameplay>
  <rating_count_gameplay>{int}</rating_count_gameplay>
  <gamestatus>{int}</gamestatus>
  <gametype>{int}</gametype>
  <gamegenre>{text}</gamegenre>
  <description>{text}</description>
</gamestat>
```

All endpoints below accept `username`(def `foo`) and `gamename` via form or query.

| Endpoint (GET/POST) | Extra params | Behavior / response |
|---|---|---|
| `/user/flex_get_game_stats` | — | Return `<gamestat>` for the row. |
| `/user/flex_bump_play_counter` | `bumpplayscount`, `bumpcompletionscount` (ints, def 0) | Add to counters if either non-zero; return updated `<gamestat>`. |
| `/user/flex_log_play` | same as bump | Identical to `flex_bump_play_counter`. |
| `/user/flex_update_game_metadata` | `gamestatus`,`gametype`(ints), `gamegenre`,`description` | Update those fields; return `<gamestat>`. |
| `/user/flex_delete_gamestatus_if_exists` | — | Delete the row, then re-create zeroed; return `<gamestat>`. |
| `/user/flex_record_rating` **and** `/user/flex_rate_game` | `graphicsrating`,`gameplayrating`(ints), `ratername` | Add ratings to totals; increment each count by 1 only if that rating > 0. Return: `<status>1</status><rating><user>{u}</user><game>{g}</game><ratername>{r}</ratername></rating>`. |
| `/user/flex_get_ratings` | `ratername` | `<user>{u}</user><game>{g}</game><grme>{ratername}</grme><gpme>{ratername}</gpme><graphics_average>{avg:0.00}</graphics_average><graphics_count>{n}</graphics_count><gameplay_average>{avg:0.00}</gameplay_average><gameplay_count>{n}</gameplay_count>`. |
| `/user/flex_list_games_by5` | `limit`(def 5),`offset`(def 0),`order`(def `plays`),`gamestatus`,`gametype` | See below. |

`flex_list_games_by5` ordering: `plays`/`mostplays`/`plays_counter` → by plays desc then recency; `rating`/`rated` → by (graphics_total + gameplay_total) desc then recency; otherwise by recency desc. Filter by `gamestatus`/`gametype` unless the value is empty/`-1`/`all`. Response:
```
<resultcount>{rows returned}</resultcount><gamecount>{total matching}</gamecount><gamestats>{…<gamestat> blocks…}</gamestats>
```

## 7. Fixed-response stub endpoints

All `text/xml`. These prevent client 404s and must return exactly:

| Endpoint(s) (GET/POST unless noted) | Response body |
|---|---|
| `/user/flex_get_conversations` | `<conversations></conversations>` |
| `/user/flex_get_message_thread` | `<messages></messages>` |
| `/user/flex_delete_message_thread` | `<status>1</status>` |
| `/user/flex_send_message` (form `recipient`) | `<status>1</status>` |
| `/user/flex_list_friendships` | `<friendships></friendships>` |
| `/user/flex_add_friendship`, `/user/flex_delete_friendship` | `<status>1</status>` |
| `/user/flex_get_wallposts` | `<wallposts></wallposts>` |
| `/user/flex_add_wallpost`, `/user/flex_delete_wallpost` | `<status>1</status>` |
| `/user/flex_get_highscores` | `<highscores></highscores>` |
| `/user/flex_submit_highscore` | `<status>1</status>` |
| `/user/flex_award_tutorial_badge` | `<status>1</status>` |
| `/user/flex_get_badges` | `<badges></badges>` |
| `/user/uploadUserImageFile` (POST) | `<status>1</status><url>/placeholder.png</url>` |

## 8. `/play` URL-rewrite rules (client origin mapping)

The client has hard-coded production origins; Ruffle rewrites them to local paths. Your `/play` page must reproduce these mappings (and the local routes above must satisfy them):

- `https?://s3.amazonaws.com/soap` → `/soap`
- `http://50.18.54.95:3000/user/{flexlogin|flexcreateuser|flexlogout|flex_heartbeat_safe|get_user_stats|flex_browse_users}` → corresponding `/user/...`
- `http://50.18.54.95:3000/log/logbug` → `/log/logbug`
- `http://50.18.54.95:3000/(.*)` → `/$1`
- `https?://s3.amazonaws.com/apphost/storage/(.*)` → `/$1` and `https?://s3.amazonaws.com/apphost/(.*)` → `/$1`

So the SOAP, Rails-style, and `apphost/storage` asset URLs all collapse onto your local routes.

## 9. Implementation notes for C#

- Prefer minimal-API/Kestrel. Force responses to **exact** byte content; disable automatic XML formatting/declarations on the fragment endpoints.
- Read form fields case-sensitively as named; for stats/game endpoints, fall back to query string.
- Keep original S3 keys verbatim through PUT→LIST→GET→DELETE; never leak your on-disk naming. Implement overlay + per-key tombstone semantics for the piece store.
- Seed the three accounts on first run; treat empty-password archive-derived accounts as password-bypass.
- Use invariant culture for the `0.00` averages and the UTC `…T…:….000Z` timestamps.
- For piece **bodies and metadata semantics** (PNG tiles, zlib+writeUTF actors/maps/tutorials/profiles, `width/height/tilename/...` meta), follow `FORMATS.md`; for the archive/data directory model and S3-key resolution, follow `server/README.md`. The server treats bodies as opaque base64 blobs — it never decodes piece formats — so you can store them however you like as long as the SOAP round-trip is byte-identical.
