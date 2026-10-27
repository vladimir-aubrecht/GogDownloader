# Steam curator DRM metadata

The optional `SteamCuratorDrmSource` reads the public
[DRM-Free Complete List](https://store.steampowered.com/curator/45614612/) curator. Its description
identifies it as a PCGamingWiki-derived list with manual additions; its automated updates are
currently labelled broken. It is a secondary community report, not a direct wiki query or a
test of an archived build. No API key, Steam login or PCGamingWiki account is needed.

The observed endpoint is
`https://store.steampowered.com/curator/45614612/ajaxgetfilteredrecommendations/`, with `start`,
`count=100`, `sort=recent`, `l=english` and `filter=all`. It returns JSON fields `success`,
`start`, `pagesize`, `total_count` and `results_html`. Recommendations are HTML fragments:
`div.recommendation`, one `a.recommendation_link` with an exact Steam `/app/ID/` URL,
`div.recommendation_desc`, and platform icons such as `span.platform_img.win`.
This is an internal storefront endpoint, so markup changes must fail safely.

The endpoint and all 19 pages were checked on 2026-10-27: 1,813 unique recommendations with
consistent totals. Wolfenstein 3D (2270) is listed. Age of Empires II (2013) (221380) is absent;
that absence means unknown, not protected. The research inspected the public endpoint and
[an existing consumer](https://github.com/ESBDRT/STEAM-DRM-FREE-Library-Checker); application
code is an independent implementation and no upstream scraper code is included.

Only the complete phrases `DRM-free`, `DRM-free.` or `DRM-free, according to PCGamingWiki.`
(case-insensitive) become Free for Windows, and only with a Windows platform icon. Additional
conditions, unknown descriptions, HTML markup and missing entries remain Unknown. Native
Linux/macOS releases are not classified. The adapter never returns Protected. Curator summaries
can omit qualifications present on the wiki; follow the evidence link or record a manual test.

Fetch the complete list before returning any classifications. Validate successful responses,
start offsets, stable totals, exact page counts, unique AppIDs and required markup. Requests
are sequential, at least 1.1 seconds apart, with a 20-second request/body timeout, 2 MiB per-page
limit, 100-page/10,000-entry ceilings and 4,096-character description limit. No retry loop. The list is reused for all lookups in that source
instance. Per-product reports use the existing seven-day snapshot cache and `--refresh` override.
Partial/invalid/denied responses become a reported refresh failure; manual corrections survive.

`DrmInfo.Source` is `steam-curator:45614612`; `Url` links to the curator with `appid`, `Notes`
retains the report and scope, and `CheckedAt` records retrieval time, not the curator's test date.
Reports apply only to the public branch. Choose the source explicitly with
`library drm sync --source steam --drm-source steam-curator`; `pcgamingwiki` remains the default.
