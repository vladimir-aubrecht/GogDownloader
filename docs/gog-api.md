# GOG API — ověřená reference

GOG nemá veřejnou dokumentaci API. Tenhle dokument je **cache ověřených faktů**, aby se
endpointy nemusely znovu dohledávat. Ověřeno 2026-07-09 proti [heroic-gogdl][gogdl]
(commit na větvi `main`), což je downloader pohánějící Heroic Games Launcher.

Refresh: `scripts/refresh-gog-api-ref.sh` (stáhne zdroje a vypíše aktuální endpointy).

[gogdl]: https://github.com/Heroic-Games-Launcher/heroic-gogdl

## Hosty

| Konstanta | Hodnota |
|---|---|
| `GOG_AUTH` | `https://auth.gog.com` |
| `GOG_EMBED` | `https://embed.gog.com` |
| `GOG_API` | `https://api.gog.com` |
| `GOG_CONTENT_SYSTEM` | `https://content-system.gog.com` |
| `GOG_CDN` | `https://gog-cdn-fastly.gog.com` |
| `GOG_CLOUDSTORAGE` | `https://cloudstorage.gog.com` |

> Pozor: CDN host je `gog-cdn-fastly.gog.com`, **ne** `cdn.gog.com`.

## Autentizace (OAuth2)

Veřejné credentials Galaxy klienta (žijí v otevřeném zdrojáku gogdl):

- `client_id` = `46899977096215655`
- `client_secret` = `9d85c43b1482497dbbce61f6e4aa173a433796eeae2ca8c5f6129f2dc4de46d9`
- `redirect_uri` = `https://embed.gog.com/on_login_success?origin=client`

Výměna kódu za token:

```
GET {GOG_AUTH}/token?client_id=…&client_secret=…&grant_type=authorization_code
    &redirect_uri=…&code={code}
```

Obnova tokenu:

```
GET {GOG_AUTH}/token?client_id=…&client_secret=…&grant_type=refresh_token
    &refresh_token={refresh_token}
```

Access token má krátkou platnost (řádově hodina), refresh token dlouhou.

## Knihovna a metadata

| Účel | Endpoint |
|---|---|
| Seznam vlastněných `gameId` | `GET {GOG_EMBED}/user/data/games` |
| Detaily hry + offline instalátory | `GET {GOG_EMBED}/account/gameDetails/{id}.json` |
| Produktová data | `GET {GOG_API}/products/{id}` (`?expand=…`) |

## Content system (Galaxy depoty) — Path A

Seznam buildů pro platformu. `build_platform` ∈ `windows`, `osx`, `linux`:

```
GET {GOG_CONTENT_SYSTEM}/products/{gameId}/os/{build_platform}/builds?&generation={1|2}
```

> Původně jsem tenhle endpoint uváděl bez segmentu `/os/{platform}/` — **to bylo špatně.**

Odpověď má `items[]`; každá položka nese `branch` a `repository_manifest` (přímé URL).
Když je `items` prázdné, hra content system nepodporuje → depot cesta není možná.

Manifesty jsou **zlib-komprimovaný JSON** (`zlib.decompress(data, 15)`).

### galaxy_path

Hash manifestu se na CDN mapuje na cestu vložením dvou adresářových úrovní:
`abcdef…` → `ab/cd/abcdef…` (pokud hash už lomítko neobsahuje).

| Účel | URL |
|---|---|
| Depot manifest (v2) | `{GOG_CDN}/content-system/v2/meta/{galaxy_path(manifest)}` |
| Patch manifest (v2) | `{GOG_CDN}/content-system/v2/patches/meta/{galaxy_path(manifest)}` |
| Depot manifest (v1) | `{GOG_CDN}/content-system/v1/manifests/{gameId}/{platform}/{timestamp}/{manifest}` |

Generation 1 je starší formát — manifest je **nekomprimovaný JSON** a cesta obsahuje
`timestamp` z produktových dat. Netriviální rozdíl oproti v2, počítat s oběma.

### Schéma manifestů v2

Repository manifest (URL z `build.repository_manifest`) po zlib dekompresi:

| Pole | Význam |
|---|---|
| `baseProductId` | id základní hry |
| `buildId` | potřeba pro patch endpoint |
| `installDirectory` | jméno instalačního adresáře (**nepoužívat pro náš zip**) |
| `dependencies[]` | id redistributables |
| `products[]` | `productId` + `name` základní hry **a všech jejích DLC** |
| `depots[]` | viz níže |

Seznam DLC se tedy nikde nedotahuje zvlášť: vezme se `products[]` z manifestu, vyhodí
se `baseProductId` a zbytek se protne s vlastněnými id z `{GOG_EMBED}/user/data/games`.

Depot: `manifest` (hash), `productId`, `languages[]`, `osBitness`, `size`,
`compressedSize`. Depot patří do stažení, když `productId` == základní hra nebo
vlastněné DLC, **a zároveň** `languages` obsahuje `*` nebo cílový jazyk.

Depot manifest (`{GOG_CDN}/content-system/v2/meta/{galaxy_path(depot.manifest)}`,
opět zlib) má `depot.items[]`, každý s `type`:

- `DepotFile` — `path`, `chunks[]`, volitelně `md5`, `sha256`, `flags[]`
- `DepotDirectory` — jen `path`, adresář k vytvoření
- `DepotLink` — `path` + `target`, symlink

`flags` obsahuje `support` (soubor patří do `gog-support`, ne do hry) a `executable`.
Cesty v `path` používají `\` jako oddělovač — na Linuxu je nutné je přeložit.

Chunk: `compressedMd5`, `md5`, `size`, `compressedSize`. Identita souboru je
`md5`, a když chybí, `chunks[0].md5` (platí jen pro jednochunkové soubory).

### Secure links (podepsané URL pro chunky)

```
generation 2:  GET {GOG_CONTENT_SYSTEM}/products/{gameId}/secure_link?_version=2&generation=2&path=/
generation 1:  GET {GOG_CONTENT_SYSTEM}/products/{gameId}/secure_link?_version=2&type=depot&path=/
```

Vrací pole `urls`. Secure link se získává **zvlášť pro každý vlastněný DLC produkt**,
ne jen pro základní hru. (Volitelné `&root=/patches/store` pro patche.)

Každý prvek `urls[]` má `url_format` a `parameters`. URL chunku se sestaví takto —
pořadí kroků je podstatné:

1. `parameters["path"] += "/" + galaxy_path(chunk.compressedMd5)`
2. v `url_format` nahradit každé `{klíč}` hodnotou `parameters[klíč]`

Tělo chunku je **zlib-komprimované**. MD5 se kontroluje proti `compressedMd5`, tedy
nad *staženými* bajty před dekompresí, ne po ní.

`401` u chunku znamená vypršelý secure link → vyžádat znovu, není to chyba stahování.

### Dependencies / redistributables

| Účel | Endpoint |
|---|---|
| Repozitář závislostí | `GET {GOG_CONTENT_SYSTEM}/dependencies/repository?generation=2` |
| Store link | `GET {GOG_CONTENT_SYSTEM}/open_link?generation=2&_version=2&path=/dependencies/store/` |

## Linux — zvláštní cesta

**Pro Linux neexistují Galaxy depoty.** gogdl pro `platform == "linux"` content system
úplně obchází a bere offline MojoSetup instalátory z `gameDetails`
(filtruje `installer["os"] == "linux"`, vybírá podle `installer["language"]`,
anglické instalátory bývají multilanguage).

Zásadní trik, který gogdl používá: MojoSetup instalátor je shell skript se ZIP payloadem,
a ZIP má central directory na konci. Díky **HTTP Range requestům** lze přečíst central
directory a stáhnout jen potřebné entries — **instalátor se nikdy nestahuje celý.**
(Zdroj: komentář v `gogdl/dl/managers/linux.py`: „new zip method based on Range headers".)

To přesně splňuje požadavek „neskladovat instalátory".

Kuriozita: `linux.py` si i pro Linux hru vyžádá `os/windows/builds`. Nezjišťoval jsem proč —
pravděpodobně kvůli zjištění verze/existence titulu. Neopisovat naslepo.

## Co gogdl neumí

Hry bez content systemu (prázdné `builds.items`) gogdl na Windows/macOS **nepodporuje** —
skončí chybou. Tj. i referenční implementace rezignuje na cestu přes Inno Setup
instalátory. Pokud takové tituly v knihovně budou, jsme na to sami (innoextract / Wine).

Kolik jich reálně je, se zjistí až po `library sync` — viz zadání: neimplementovat,
dokud se neprokáže potřeba.
