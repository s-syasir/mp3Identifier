# mp3Identifier

Identifies mp3s by audio fingerprint (ignoring whatever tags are already on
the file), looks up the real artist/album/title/genre/track-number/cover art
via AcoustID + MusicBrainz, and writes the corrected tags back. Two modes:
a drag-and-drop web UI for one file at a time, and a recursive folder scanner
for batch-organizing a whole directory.

## Setup

1. Install the fingerprinting tool:
   ```bash
   sudo apt install libchromaprint-tools
   ```
2. Get an AcoustID API key, either:
   - **Just trying it out:** use AcoustID's own public test key from their docs
     (https://acoustid.org/webservice), `u3yzIMx9jAA`. It's meant exactly for
     this, no signup needed, but it expires after a few days, so don't rely
     on it beyond a quick test.
   - **Actual use:** register your own free, permanent key at
     https://acoustid.org/new-application (log in with Google/MusicBrainz/OpenID,
     no payment or approval wait, key is issued instantly).
3. Store the key with user-secrets, keeps it out of git and appsettings.json:
   ```bash
   dotnet user-secrets set "AcoustId:ApiKey" "u3yzIMx9jAA"
   # or your own registered key instead of the test one
   ```

## Usage

**Web UI** — one file at a time, with a preview/edit step before anything is written:
```bash
bash runUI.sh
```
Opens on the URL it prints (`http://localhost:5055` by default). Drag a file
onto the page or use Browse Files, review the suggested tags and cover art,
edit anything before applying, then Apply writes the tags, renames the file,
and offers a Download link.

**Folder scan** — recursive, fully automatic, no UI:
```bash
bash runFolder.sh /path/to/folder
```
Finds every `.mp3` under that folder, identifies and tags each one
automatically (no confirmation step), and organizes the results into
`<folder>/Music/<Artist>/<Album>/`. Run with no argument for a usage message.

Use `bash script.sh`, not `./script.sh` — this project sits on a noexec-mounted
path, so directly executing a file here fails regardless of its permission bits.

## Naming convention

Tagged files are renamed to:
```
Artist - Album - TrackNumber - Title.mp3
```
(track number is omitted if MusicBrainz doesn't have a track position for
that recording).

## How it works

- `fpcalc` (from `libchromaprint-tools`) generates a Chromaprint fingerprint
  for the file.
- That fingerprint is looked up via `GET https://api.acoustid.org/v2/lookup`
  (must be GET, not POST — AcoustID's server mishandles the `meta` param's
  `+` delimiters over POST). The response gives title/artist/release group/year
  directly; when a match's `recordings` array bundles multiple distinct tracks
  off the same album (a known AcoustID data quirk), the one whose own duration
  is closest to the file's actual length is picked, with a small penalty
  against alternate versions ("(instrumental)", "(radio edit)", etc.) so an
  exact-duration outlier doesn't beat the plain title.
- Genre and track-number/total aren't in AcoustID's response at all, those
  come from two follow-up MusicBrainz calls (`release-group?inc=genres` and
  `release?inc=recordings`). Cover art comes from the Cover Art Archive, keyed
  by release id, and is embedded into the file's ID3 tag on apply.
- A release's `releases[]` list isn't sorted chronologically (mix of the
  original release and every later reissue), so the earliest date is used for
  the year rather than trusting index 0.
- TagLib# reads the file's current (possibly wrong) tags for comparison and
  writes the corrected ones back on apply.
- Before any tag edit, the pre-edit file is copied to a backup location
  (`/tmp/mp3identifier/backup/` for the web UI, `<folder>/Music/_backup/` for
  folder scan) so nothing is destructively lost.

## Known limitations

- Always takes AcoustID's top-scored match, no manual disambiguation UI when
  multiple candidates are close in score.
- Genre is whichever MusicBrainz community tag has the most votes on the
  release group, a plausible best guess, not guaranteed accurate.
- Folder scan only matches `*.mp3` currently, not other audio formats.
- Rate limit is 3 req/sec against AcoustID; fine for interactive/one-off use,
  folder scan doesn't currently throttle, so a very large folder could hit it.
