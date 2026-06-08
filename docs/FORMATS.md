# MyGameBuilder archive - piece file formats

This document is a companion to [`README.md`](./README.md). It describes
the byte-level format of every archived object body, so the archive can
be read without the original Flash client.

Every archived object has a `piecetype` (the directory name it lives in)
that determines how its body bytes are interpreted. The archive stores
the body **exactly as it was served from S3** - i.e. the same bytes the
client receives before any decoding.

The `x-amz-meta-*` headers (mirrored under `amz_meta` in each
`.meta.json`) carry a small set of common fields that complement the
body for every piece type:

| `amz_meta` key  | Meaning |
| --- | --- |
| `width`, `height` | Logical size of the piece (pixels for tiles, cells for maps, etc.). |
| `tilename`        | For actors and maps: the name of the tile piece used to draw it. Empty otherwise. |
| `blobencoding`    | Always `"0"` for archived bodies. |
| `comment`         | Author-supplied description, if any. |
| `acl`             | Original S3 ACL string (typically `"null"` or `"public-read"`). |

The `content_type` recorded in the sidecar is `image/png` for tiles and
`text/plain` for the other piece types (the client looks at
`piecetype`, not the HTTP type, to decide how to parse the body).


For the piece types whose raw body is not in a human-readable format
(actor, map, tutorial, profile), the archive also stores a **decoded
sibling** file next to the raw body — `<name>.xml` for actors,
`<name>.json` for maps, and `<name>.txt` for tutorials and profiles.
The decoded siblings are derived artifacts produced from the raw body
using the rules below; the raw body remains the canonical archived
form. See [`README.md`](./README.md) §3.4 for the sibling-file layout. All user and system folders are under `JGI_test1/`, and the client snapshot is under `client/`.

---

## 1. `tile/` - PNG image

Tile bodies are **plain PNG files**, byte-for-byte. They start with the
standard PNG signature (`89 50 4E 47 0D 0A 1A 0A`) and can be opened
with any image tool. `amz_meta.width` / `height` mirror the PNG's pixel
dimensions. There is no extra wrapping or base64 layer in the archive;
the file under `…/tile/<name>.png` *is* the image. (The `.png` suffix
is appended on disk so the file previews directly; the S3 key itself
does not include it — read the sidecar's `key` field for the original
S3 key.)

(See `README.md` §6 for the subset of tile bodies that were intentionally
replaced.)

---

## 2. `actor/` - zlib-compressed XML

Actor bodies are a single **zlib stream** (the file starts with bytes
like `78 9C` / `78 DA`). After decompression the bytes are an AS3
`ByteArray.writeUTF` payload:

```
uint16 be   length of the UTF-8 string that follows
bytes[len]  UTF-8 text
```

The UTF-8 text is an XML document describing the actor. One quirk:
every `<` and `>` in the original XML has been replaced with `{{{` and
`}}}` respectively before storage, presumably to keep the payload from
being mangled by intermediaries that scanned for HTML. To read it as
XML, decompress, skip the two-byte length prefix, then replace `{{{`
→ `<` and `}}}` → `>`.

`amz_meta.width` and `amz_meta.height` are always `"0"` for actors;
the actor's visual size comes from the tile(s) it references, not from
the actor object itself.

The recovered XML always has this shape:

```xml
<actor>
  <databag>
	<all>            ... fields that apply to every actor ...           </all>
	<allchar>        ... fields shared by Player, NPC, and Shot actors ...</allchar>
	<playercharacter/>                                  <!-- see below -->
	<npc>            ... NPC-only fields (dialog, movement, etc.) ...   </npc>
	<item>           ... Item-only fields (activation, equipment, etc.) ...</item>
	<itemOrNPC>      ... fields shared by NPC and Item actors ...       </itemOrNPC>
  </databag>
  <animationTable>row1#row2#...#rowN</animationTable>
</actor>
```

`<databag>` always carries the full set of group elements, even when
the actor's type doesn't use them - e.g. an NPC's XML still contains
the `<item>` section with default values. The `<actorType>` element
inside `<all>` decides which sections are actually meaningful:

| `actorType` | Meaning   |
| --- | --- |
| `0` | Player |
| `1` | NPC (Non-Player Character) |
| `2` | Item, wall, or scenery |
| `3` | Shot |

`<playercharacter>` is part of the actor XML schema template
(`ActorMakerControl._ActorMakerControl_XML1_i()`) but no code in the
decompiled Flash client reads or writes any field inside it. In every
archived actor observed it is emitted as an empty element. Treat it as
a reserved schema slot.

A non-exhaustive map of the inner fields, taken from the AS3 source:

- `<all>`: `actorType`, `description`, `initialHealthNum`,
  `initialMaxHealthNum`, `gravityYN`, `soundWhenHarmed`,
  `soundWhenHealed`, `soundWhenKilled`, `visualEffectWhenHarmedType`,
  `visualEffectWhenHealedType`, `visualEffectWhenKilledType`.
- `<allchar>` (Player, NPC, Shot): `movementSpeedNum`, `upYN`, `downYN`,
  `leftYN`, `rightYN`, `shotRateNum`, `shotRangeNum`,
  `soundWhenShooting`, `shotActor`, `pushYN`, `jumpYN`,
  `shotDamageToPlayerNum`, `shotDamageToNPCorItemNum`,
  `touchDamageToPlayerNum`, `touchDamageToNPCorItemNum`,
  `touchDamageAttackChance`, `touchDamageCases`.
- `<npc>`: `movementType` (0=none, 1=random, 2=toward player,
  3=away from player), `canOccupyPlayerSpaceYN`, `shotAccuracyType`,
  `talkText`, `talkTextFontIndex`, and three dialog response slots
  (`responseChoice1..3` plus their `takesObject*`, `dropsObject*`,
  `saysWhatOnChoice*`, `responseChoice*StayYN` siblings).
- `<item>`: `itemActivationType` (0..9 - see `alItemActivation` in
  `MgbActor.as`), `inventoryEquippableYN`, `inventoryEquipSlot`,
  `visualEffectWhenUsedType`, `pushToSlideNum`, `squishPlayerYN`,
  `squishNPCYN`, `healOrHarmWhenUsedNum`, `increasesMaxHealthNum`,
  `gainExtraLifeYN`, `gainOrLosePointsNum`, `winLevelYN`,
  `gainPowerType`, `gainPowerSecondsNum`, `useText`,
  `itemPushesActorType`, `itemPushesActorDistance`, `keyForThisDoor`,
  `keyForThisDoorConsumedYN`, `autoEquipYN`, `equippedNewActorGraphics`,
  `equippedNewShotActor`, `equippedNewShotSound`,
  `equippedNewShotDamageBonusNum`, `equippedNewShotRateBonusNum`,
  `equippedNewShotRangeBonusNum`, `equippedArmorEffect`.
- `<itemOrNPC>`: `destroyableYN`, `scoreOrLosePointsWhenShotByPlayerNum`,
  `scoreOrLosePointsWhenKilledByPlayerNum`, `dropsObjectWhenKilledName`
  (+`Chance`, +`Name2`, +`Chance2`), `dropsObjectRandomlyName`
  (+`Chance`), `respawnOption` (0=respawn on map reload, 1=never),
  `appearIf`, `appearCount`, `conditionsActor`.

Naming conventions used inside the XML:

- `…YN` = `"0"` / `"1"` boolean.
- `…Num` = integer (decimal in the text content).
- `…Type` = small integer that indexes into a named lookup table in
  `MgbActor.as` (`alMovementType`, `alItemActivation`,
  `alItemPushesActorType`, `alVisualEffect`, `alShotAccuracy`, …).
- `soundWhen…` / `equippedNewShotSound` = the literal string `"none"`
  or one of the canned sound names in `MgbActor.alSoundsArray`.
- Empty references (`shotActor`, `keyForThisDoor`,
  `dropsObjectWhenKilledName`, …) carry an empty element value.

### Animation table

The `<animationTable>` element holds the actor's animation map as a
single string. Rows are separated by `#` and each row is three
`|`-separated fields:

```
action | tilename | effect
```

- `action`   - the named animation slot. There are 132 slots, indexed
  by `MgbActor.getDGArrayItemIndex` in fixed order: `face north`,
  `step north 1..4`, `face east`, `step east 1..4`, `face south`,
  `step south 1..4`, `face west`, `step west 1..4`, `stationary 1..16`,
  `melee north 1..8`, `melee east 1..8`, `melee south 1..8`,
  `melee west 1..8`, `stationary north 1..16`, `stationary east 1..16`,
  `stationary south 1..16`, `stationary west 1..16`.
`tilename` - the name of a tile piece (resolved in the actor's own
project first, then `!system`). Empty means "no frame for this
animation slot". All user and system folders are under `JGI_test1/`.
- `effect`   - rendering hint for the frame. Observed values are
  `"no effect"` (overwhelmingly the most common), `"rotate90"`,
  `"rotate180"`, `"rotate270"`, `"flipX"`, `"flipY"`, and the empty
  string (used interchangeably with `"no effect"`).

Actors typically write all 132 animation slots, but some older or
hand-trimmed actors only persist the first 20, 36, or 68 rows; missing
rows are filled with defaults at load time.

---

## 3. `map/` - zlib-compressed layered grid

Map bodies are also a single **zlib stream**. After decompression the
bytes are a tiny binary format written with AS3 `ByteArray`
(big-endian):

```
int32         layerCount               (4 in every archived map, or 0)
repeat layerCount times:
	int32     cellCount                (== width * height for this layer,
										or 0 if the layer is unused)
	repeat cellCount times:
		utf   cell                     (writeUTF: uint16 length + UTF-8 bytes;
										empty string means "empty cell")
```

`width` and `height` are not in the body itself; they are read from
`amz_meta.width` / `amz_meta.height`. Cells are stored in row-major
order (`index = y * width + x`).

There are always **four layers**, in this exact order:

| Index | Name         | Cell content |
| ---: | --- | --- |
| 0 | Background | Name of a tile-actor (drawn under everything). |
| 1 | Active     | Name of an interactive actor (Player, NPC, Item, Shot). |
| 2 | Foreground | Name of a tile-actor (drawn over the Active layer). |
| 3 | Event      | Event marker for that cell (see below). |

Layers 0–2 store **actor names** that reference actor pieces in the
same project (or under `!system` in `JGI_test1/`); an empty UTF string means the cell
on that layer is empty. The fourth "Event" layer is **persisted**, not
just runtime state: it is part of the saved blob whenever a map is
savd. It is, however, often empty - many maps carry the layer with
all cells blank.

The Event layer's cell strings are a small ad-hoc DSL parsed by
`CommandEngine.parse`. The grammar is:

```
command: key=value,key=value,…
```

with `command` (no colon, no parameters) also allowed for tag-style
events. Recognized commands in the client are:

- `jump: mapname=<name>,x=<col>,y=<row>[,effect=<name>]` - transitions
  to the named map (or to the current map's name if omitted) at the
  given destination cell. `mapname` and `effect` are optional.
- `music: source=<path>,loops=<count>` - plays a music track from
  `s3.amazonaws.com/JGI_assets/` while the map is active. The empty
  source stops music.
- `raiseSkillLevel: skillname=<name>,newMinLevel=<n>` - bumps a user
  skill level (used by the tutorial system).

Any cell string that does not start with one of those `command:`
prefixes is treated as a bare event tag. Examples actually observed in
archived maps include `finish game`, `dj dog`, and `simon`; published
games and the in-game controllers react to these tags.

A `layerCount` of `0` is also valid and represents a freshly created,
never-saved map; in that case no per-layer data follows. Empty cells
within otherwise non-empty layers are stored as zero-length UTF
strings.

---

## 4. `tutorial/` - zlib-compressed step list

Tutorial bodies are a **zlib stream** wrapping a single
`ByteArray.writeUTF` payload (same `uint16 length + UTF-8 bytes` shape
as actors). The string is a flat list of steps:

```
step1#step2#…#stepN
```

The number of steps is also recorded as `amz_meta.height` (with
`width = "1"`). Each step is exactly four `|`-separated fields:

```
message | graphic | completionTag | rewardResult
```

- `message`        - the text shown to the user for this step. May
  contain a small amount of HTML (e.g. `<b>…</b>`); newlines are
  rendered as in Flex's `TextArea`.
- `graphic`        - name of an illustrative tile to show alongside
  the message, or empty.
- `completionTag`  - the tag the client watches for to advance the
  step. The special value `(nextButton)` means "advance when the user
  clicks the Next button"; other values (e.g.
  `tilemaker_choose_pen`, `tilemaker_use_eraser`) are completion
  events fired from elsewhere in the UI.
- `rewardResult`   - reward granted when the step completes, or empty
  (most archived tutorials leave this blank and rely on the global
  badge logic instead).

Steps are produced by `MgbTutorial.save()` with `(!!i ? "#" : "") +
…`, so there is no leading or trailing `#`; some hand-edited archived
tutorials nonetheless end with a trailing empty field (an extra `|`
before the next `#`), which decodes as an empty `rewardResult`.

---

## 5. `screenshot/` - PNG image

Screenshots are PNGs of a rendered map, stored the same way as tiles
(raw PNG bytes, no wrapping, with a `.png` suffix appended to the S3
leaf on disk). They live next to a project's `map/` pieces and are
produced by the in-game "publish" flow.

---

## 6. `profile/` - per-user key/value blob


Every user has exactly one `profile` piece, named `user`, under the
reserved project `-`, and all user and system folders are under `JGI_test1/`:

```
JGI_test1/<username>/-/profile/user
JGI_test1/<username>/-/profile/user.meta.json
```

The body is a **zlib stream** that decompresses to a `writeUTF`
payload (same `uint16 length + UTF-8 bytes` shape as actors and
tutorials). The string is a flat list of records separated by `#`,
each record being a `key|value` pair:

```
#key1|value1#key2|value2#…#keyN|valueN
```

`MgbProfile.save()` always emits a leading `#` (the "first record"
flag in the code is never cleared), so the decoded string in practice
always starts with one. Keys never contain `#` or `|`; values never
contain `#`. Order is not significant. Common keys, all text-typed:

- **UI background colors** (hex strings like `0xD0D0D0`):
  `mainBackgroundColor`, `tilemakerBackgroundColor`,
  `actormakerBackgroundColor`, `mapmakerBackgroundColor`,
  `gamemakerBackgroundColor`, `gameplayerBackgroundColor`,
  `tutorialmakerBackgroundColor`, `accountmanagementBackgroundColor`,
  `informationpanelBackgroundColor`, `adpanelBackgroundColor`.
- **Skill levels** (integers):
  `skillLevelTileMaker`, `skillLevelActorMaker`, `skillLevelMapMaker`,
  `skillLevelGameMaker`, `skillLevelGamePlayer`,
  `skillLevelTutorialMaker`, plus
  `skillLevelCurrentTileMaker` / `skillLevelCurrentMapMaker`.
- **Quota / session**: `maxQuotaKB` (e.g. `1024`, `16384`),
  `lastLoginDate` (`HH:MM:SS MM/DD/YYYY (GMT±N)`).
- **Profile text**: `userStatusComment`, `tileCopyAllowedFlag`
  (`"yes"`/`"no"`), and `profile-<field>` records used by the
  Account Management screen.
- **Project metadata**: `projectComment-<projectName>` - the
  description shown on the projects list for that project.
- **Tutorial progress**:
  `tutorialLevelCompleted` (the name of the last completed tutorial)
  and one `TutDone.<tutorialName>|<timestamp>` record per tutorial
  the user has finished, e.g.
  `TutDone.001 Starting Tile Maker|17:09:59 03/28/2010`.

The profile object also carries `amz_meta.width = "0"` and
`amz_meta.height = "0"`; the body is fully self-describing.
