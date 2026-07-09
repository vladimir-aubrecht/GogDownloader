# GogDownloader

Záloha vlastních her z GOG do portable zipů. C# / .NET 9, běží na Linux, Windows i macOS.

## Než sáhneš na GOG API

Přečti `docs/gog-api.md`. Je to ověřená cache endpointů — GOG nemá veřejnou dokumentaci.
**Nehledej endpointy na webu.** Obnovu dělá `scripts/refresh-gog-api-ref.sh`.

## Rozhodnutí, která už padla

- **Path A (primární):** Galaxy content-system depoty pro `windows` a `osx`. Stahují se
  rovnou soubory nainstalované hry po chunkách. Žádná konverze instalátoru.
- **Path B (Linux):** Galaxy depoty pro Linux neexistují. Linux verze jsou MojoSetup
  instalátory (shell skript + ZIP payload). Čtou se přes HTTP Range — instalátor se
  nikdy nestahuje celý. Tahle cesta je nevyhnutelná, ne spekulativní.
- **Path C (Inno Setup / Wine):** jen pro Windows/macOS tituly bez depotu. **Zatím se
  neimplementuje.** Nejdřív `library sync` ukáže, kolik takových titulů reálně je.
  V první verzi se přeskočí a nahlásí.
- **Archiv:** zip všude. Symlinky se dereferencují, unixová práva neřešíme — uživatel
  to výslovně odsouhlasil a řeší si to mimo tento projekt.
- **Spolehlivost:** práce ve staging adresáři, do cíle jen atomický `File.Move`.
  Manifest `.gogdl.json` se zapisuje **jako úplně poslední krok** — dokud není, hra je
  z pohledu toolu nestažená a restart ji udělá znovu. Poškozený „hotový" stav nevznikne.
- **Idempotence a patche:** manifest nese hashe po jednotlivých souborech. Patch stáhne
  jen chunky změněných souborů, zbytek vytáhne ze starého zipu (ten slouží jako cache).
- **Názvy adresářů** `{slug}__{gameId}`, nikdy podle titulu (`:`, `CON`/`AUX`, MAX_PATH,
  shoda napříč OS).
- **Uživatel řídí** rychlost stahování i počet souběžných spojení.

## Past, na kterou se dá naletět

`api.gog.com` používá pro macOS řetězec `mac`, ale `content-system.gog.com` používá
`osx`. Nemíchat.

## Zásady práce na tomhle projektu

- Neimplementovat featury, u kterých není prokázáno, že je knihovna uživatele potřebuje.
  Když si nejsem jistý, zeptat se — ne psát kód pro jistotu.
- Drahá zjištění ukládat do `docs/`, ne je odvozovat znovu v každé session.
- Odhady velikosti a počtů brát z reálných dat (`library sync`), ne z hlavy.

## Build

```sh
dotnet build
dotnet run --project src/GogDownloader.Cli -- <command>
```
