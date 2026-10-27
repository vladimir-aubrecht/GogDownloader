# Community DRM metadata

Without a saved session, the PCGamingWiki adapter uses the public AppID resolver
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

On 2026-10-27 the user also received HTTP 403 on the first lookup. A development request to the
resolver returned a Cloudflare challenge, and a direct MediaWiki request was denied too.
The [official API requirements](https://www.pcgamingwiki.com/wiki/API#Requirements) now document
restricted endpoints (notably Cargo queries), MediaWiki bot-password authentication, a custom
user agent and a 60-request/minute limit. The adapter now supports bot-password login as
described below. A Cloudflare refusal does not establish that a bot password alone would fix
access. Report 403 as denied automated access, without retrying the library or blaming Steam login.

## Bot-password authentication

The implemented flow follows [MediaWiki API:Login](https://www.mediawiki.org/wiki/API:Login#Using_action=login)
and PCGamingWiki's requirements (inspected 2026-10-27):

1. GET `/w/api.php?action=query&meta=tokens&type=login&format=json`, retaining its cookies.
2. POST form fields `action=login`, `format=json`, `lgname=Account@BotName`,
   `lgpassword=BOT_PASSWORD`, `lgtoken=TOKEN` to `/w/api.php` with the same cookies.
3. Require `login.result=Success`, then GET `action=query&meta=userinfo&assert=user` and
   require an authenticated identity matching the login result before saving the session.

Create the credentials at [Special:BotPasswords](https://www.pcgamingwiki.com/wiki/Special:BotPasswords).
Grant `Create, query and delete data through the Cargo extension`, as documented by PCGamingWiki
for `cargoquery`. GameArchiver only reads data; it does not use the grant to create/delete wiki data.
Use the generated bot username and password, not the main account's password.

The CLI provides `auth login|status|logout --source pcgamingwiki`. Login prompts without
echoing the password or accepts `GAMEARCHIVE_PCGW_USERNAME` / `GAMEARCHIVE_PCGW_BOT_PASSWORD`.
`--username` overrides the username environment variable. Credentials are never put in a URL,
printed in diagnostics or saved to disk. Only the verified session's cookies and account
identity are persisted, atomically, in `gamearchive/pcgamingwiki-session.json` under the user
configuration directory. The cookie file is not encrypted: Unix files are created as 0600;
Windows files are created with a protected ACL granting access only to the current user.
Status reads local state and explicitly does not claim online validation. Logout forgets the
local session; revoking the bot password on the wiki revokes all its sessions.

`library drm sync` uses this session automatically, or the file selected by `--pcgw-session`.
The first uncached lookup validates the saved identity. Authenticated requests use `assert=user`;
an expired/rejected session stops enrichment and asks for a new login, never silently degrading
to anonymous queries. No saved session preserves the existing anonymous behavior. The C# API
opts into persistence with the `sessionPath` constructor argument; `LoginAsync` also supports
in-memory sessions when no path is given. Injected HTTP clients must disable redirects and
automatic cookies, as the default transport does. The adapter manages scoped cookies itself.
Cookie changes from validated identity, Cargo and wikitext responses are saved atomically before
the next request, including renewed expirations and cookie additions/removals. Unchanged cookies
do not rewrite the file. Rejected identities, HTTP/API errors, malformed responses and cancelled
requests do not replace the last verified session. An already saved valid rotation survives a
later metadata-request failure, so the next process can reuse it without another password login.

Authenticated AppID lookup uses the documented Cargo `Game` table:
`action=cargoquery&tables=Game&fields=Game._pageID=PageID&where=Game.Steam_AppID HOLDS "APPID"&limit=2`.
Require exactly one page ID; no match or ambiguity remains unknown. Fetch wikitext by `pageid`
and still require the exact Steam AppID in the availability row. Attribution links to the
public `w/index.php?curid=PAGEID` page. This avoids the anonymous PHP redirector and page HTML.

The default transport refuses API redirects (including POST redirects), keeps cookies on the
fixed HTTPS API endpoint, bounds responses, and never echoes server-supplied login errors.
Diagnostics distinguish rejected logins, expired sessions, missing Cargo grants, rate limits
and recognizable Cloudflare blocks. No automatic login retry or challenge bypass is attempted.
Failed/cancelled logins retain the previous saved session. Controlled HTTP probes cover the
complete login/enrichment flow and failures. Live authenticated compatibility remains unverified:
no PCGamingWiki credentials are configured here, and even the login-token endpoint returned a
Cloudflare 403 from this development environment on 2026-10-27.

The CLI supports `--drm-source steam-curator` as an explicit public alternative; see
[the curator adapter](steam-curator-drm.md). It does not bypass the wiki's access protection
or silently substitute a different data source. PCGamingWiki remains the default.

The parser accepts an exact numeric Steam ID or Steam `/app/ID/` URL in an availability row.
Only an explicit `DRM-free` or `None` DRM cell yields `Free`; known DRM identifiers yield
`Protected`. Missing/unrecognized DRM, conflicting rows, incomplete templates and nonempty
qualifying notes yield `Unknown`. Template expansion is not evaluated. Comments and nowiki
examples are ignored. Unscoped PC availability is classified only for Windows. Explicit OS
qualifiers can classify Linux/macOS; no claim is copied across OSes, store editions or branches.
This intentionally leaves conditional DRM-free games unknown until manually reviewed.

Requests are sequential with a 1.1-second minimum interval, explicit user agent, 20-second
header/body deadlines and a 2 MiB API-body limit. One failure stops further requests for that
refresh. Fresh cached reports remain; unrefreshed reports become unknown without a successful
check timestamp. No browser automation, challenge bypass or implicit password login is attempted.

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

`library drm set|clear` can infer the product from archive metadata in the current directory
when no `SOURCE:ID` is supplied. It inherits the only archived platform and that platform's branch;
multiple platforms require `--platform`, and explicit platform/branch flags take precedence.
It accepts canonical and legacy filenames, uses the same ownership-conflict checks as archive
operations, and writes the correction only to the selected library snapshot. Explicit product
keys do not use local context. Neither operation needs store or community network access.

DRM does not participate in content/version equality. A new download records its selected
classification with the archive. `AlreadyDone`, enrichment and manual overrides leave existing
archive metadata unchanged; the library remains the current annotation source for filtering.
