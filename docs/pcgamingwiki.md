# Community DRM metadata

The PCGamingWiki adapter uses the public AppID resolver
`https://www.pcgamingwiki.com/api/appid.php?appid=APPID`, which redirects an exact match to a
wiki page, followed by MediaWiki `action=parse&prop=wikitext&format=json&redirects=1&page=TITLE`
at `https://www.pcgamingwiki.com/w/api.php`. A disambiguation/search response is not an exact
match. Never guess the first title, match only by name or infer DRM from store ownership.

The resolver/parse workflow and `Availability/row|Steam|APPID|DRM` layout were inspected in
[steam-library-pcgamingwiki-user-script](https://github.com/fliker09/steam-library-pcgamingwiki-user-script)
on 2026-10-26. The implementation here is independent and deliberately conservative: the
referenced script's missing-data default of `None` is **not** used. Live PCGamingWiki requests
from the development environment returned Cloudflare HTTP 403, so live-service compatibility
has not been confirmed; controlled HTTP fixtures cover redirects, responses and access denial.

The parser accepts an exact numeric Steam ID or Steam `/app/ID/` URL in an availability row.
Only an explicit `DRM-free` or `None` DRM cell yields `Free`; known DRM identifiers yield
`Protected`. Missing/unrecognized DRM, conflicting rows, incomplete templates and nonempty
qualifying notes yield `Unknown`. Template expansion is not evaluated. Comments and nowiki
examples are ignored. Unscoped PC availability is classified only for Windows. Explicit OS
qualifiers can classify Linux/macOS; no claim is copied across OSes, store editions or branches.
This intentionally leaves conditional DRM-free games unknown until manually reviewed.

Requests are sequential with a one-second minimum interval, explicit user agent, 20-second
header/body deadlines and a 2 MiB API-body limit. One failure stops further requests for that
refresh. Fresh cached reports remain; unrefreshed reports become unknown without a successful
check timestamp. No browser automation, challenge bypass or implicit login is attempted.

`DrmInfo` records status, source, public-branch scope, page URL, check time and a qualification
note. The source URL provides attribution and a place to inspect the underlying evidence;
PCGamingWiki's content terms remain applicable to its information. A community report is not
a per-build verification, a promise of offline functionality, or proof that optional online
features work. No executable scanning or DRM modification is performed.

The optional `GameInfo.Drm` dictionary and `ArchiveLibrary.DrmOverrides` are additive metadata.
Missing fields in old snapshots mean unknown for Steam and the distribution-policy default
for available GOG versions. Overrides are per product/OS with a branch qualifier; one override
per OS is retained. They are separate from community reports, so clearing an override reveals
the underlying report, and refreshing ownership does not lose a user's correction. Normal
sync does not perform community HTTP calls. `library drm sync` explicitly refreshes reports;
the seven-day cache can be bypassed with `--refresh`.

DRM does not participate in content/version equality. A new download records its selected
classification with the archive. `AlreadyDone`, enrichment and manual overrides leave existing
archive metadata unchanged; the library remains the current annotation source for filtering.
