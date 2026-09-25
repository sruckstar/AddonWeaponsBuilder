# AddonWeapons Builder (C# / Avalonia)

Превращает **replace-сборку** оружия для GTA V (модели/текстуры, названные под
существующее оружие) в **add-on DLC-пак**, который детектится нативами
`GET_NUM_DLC_WEAPONS` / `GET_DLC_WEAPON_DATA` и появляется в меню скрипта
AddonWeapons — без замены ванильных стволов. Умеет сразу установить пак в игру
через папку `mods` (OpenIV).

Это порт оригинального Python-проекта (`D:\awb`) на **.NET 10 + Avalonia 12 +
SkiaSharp**. Ядро перенесено 1:1 и сверено с оригиналом (см. «Проверка»),
интерфейс переделан.

---

## Структура

```
AddonWeaponsBuilder.slnx
src/
  Awb.Core/                  ядро (без UI), бывший пакет awb/
    Templates.cs             библиотека шаблонов из ванильных meta     ← templates.py
    Scanner.cs               классификация ассетов, резолв базового ствола ← scanner.py
    Naming.cs                уникальный неймспейс, collision-safe     ← naming.py
    MetaGenerator.cs         9 meta-файлов + сопоставление компонентов ← metagen.py
    Overrides.cs             свои meta модера / готовый dlc.rpf       ← overrides.py
    Gxt2.cs                  компилятор/ридер .gxt2                    ← gxt.py
    Rpf/                     RPF7-OPEN writer/reader/verify/patch       ← rpf.py
    DlcAssembler.cs          дерево dlcpack → dlc.rpf                   ← assembler.py
    MergedPack.cs            общий пак AddonWeapons[N], лимит 3 ГБ      ← merge.py
    GameInstaller.cs         установка в mods + dlclist.xml             ← installer.py
    ShopIds.cs               подбор свободного Shop ID                  ← shopid.py
    Pipeline.cs              оркестрация сборки (3 маршрута)            ← pipeline.py
    SourceIntake.cs          drop игрока: папка / zip / rar / 7z → плоская входная папка (новое)
    Util/EtXml.cs            XML-сериализация как у Python ElementTree
  Awb.Cli/                   awbctl — командная строка                  ← awbctl.py
  Awb.App/                   AddonWeaponsBuilder.exe — GUI              ← awb_app.py + web/
    Controls/                Skia-контролы: анимированный револьвер, эмблема
    ViewModels/              MVVM (CommunityToolkit.Mvvm)
    Services/                лог, диагностика (--diagnose), настройки
data/                        ванильные meta + templates/ (133 ствола)
tests/Awb.Tests/             xUnit-тесты ядра и ViewModel
tools/parity/                сверка с Python-оригиналом
tools/Awb.UiSnapshot/        офскрин-рендер окна в PNG (Avalonia.Headless)
installer/                   Inno Setup (тот же AppId, что у Python-версии)
build.ps1                    тесты + self-contained публикация (+ zip / установщик)
```

## Сборка и запуск

Нужен .NET SDK 10.

```powershell
dotnet run --project src/Awb.App          # GUI
dotnet test AddonWeaponsBuilder.dev.slnx  # тесты (tests/ и tools/ не входят в репозиторий)
.\build.ps1 -Zip                           # publish\AddonWeaponsBuilder (+ zip), .NET на машине пользователя не нужен
.\build.ps1 -Installer                     # + installer\out\AddonWeaponsBuilder-Setup-<ver>.exe (Inno Setup 6)
```

## Использование

### GUI

Одно окно, две вкладки-режима:

* **For Modders** — сборка в выбранную папку: упакованный `dlc.rpf` или открытые
  папки для CodeWalker; можно задать имя модели (`w_pi_mygun`).
* **For Players** — сборка и установка прямо в GTA V (`mods/update/x64/dlcpacks` +
  `dlclist.xml`), по умолчанию всё оружие складывается в один пак `AddonWeapons`.
  Источник — **drop-зона**: мод перетаскивается в окно как скачан (папка, `.zip`,
  `.rar`, `.7z`, `.oiv`, несколько файлов сразу) или выбирается кнопками
  «Choose folder… / Choose archive…». Подробнее — «Источник в режиме игрока».

Слева — источник, назначение, параметры оружия, цены компонентов (поле на каждый
найденный магазин/глушитель/прицел). Справа — **анализ источника** (какой маршрут
сборки, главная модель, базовое оружие, hi-lod, компоненты, предупреждения) и
**живой лог сборки**. Во время сборки — анимированный револьвер (SkiaSharp), подпись
показывает реальную фазу сборки. Результат — баннер с кнопкой «Open folder».
Светлая/тёмная тема, выбранный режим и последние папки запоминаются.

### Командная строка

```bash
awbctl build-templates [data_dir] [out_dir]
awbctl scan  data/templates <input_folder>
awbctl plan  data/templates <input_folder> --name "My Weapon"
awbctl build data/templates <input_folder> <out_dir> \
    --name "Vintage Pistol" --desc "A refined classic sidearm." \
    --price 45000 --ammo-cost 120 --comp-price w_pi_x_mag1=800 \
    [--model-name w_pi_mygun] [--no-pack] [--merge-pack] [--install-game-dir "D:\GTA V"]
awbctl verify <archive.rpf>        # самопроверка ресурсов готового архива (новое)
```

## Источник в режиме игрока (drop-зона)

`SourceIntake` распаковывает то, что бросили в окно, в `%LOCALAPPDATA%\AddonWeaponsBuilder\sources\<id>\`
(хранится только последний drop) и собирает из него плоскую входную папку, которую
конвейер обрабатывает как обычно — сам конвейер не менялся.

* **Архивы**: zip, rar (включая RAR5), 7z (включая solid), `.oiv` (это zip), многотомные
  (`.part1.rar`, `.7z.001`), архивы внутри архивов — до 3 уровней. Из архива извлекается
  только нужное (модели, `.rpf`, текст, вложенные архивы); картинки и прочее не трогаются.
  Защита от zip-slip, лимит 8 ГБ распакованного, понятные ошибки для запароленного или
  битого архива.
* **Модели и текстуры** (`.ydr/.ytd/.ydd/.yft` с заголовком RSC7) ищутся на любой глубине.
  Если в моде есть `w_*`-ассеты, прочие (`hud.ytd`, педы, пропы) пропускаются. Папки-бэкапы
  оригинала (`Original`, `Vanilla`, `Backup`, `Old`, `Default`, `Uninstall`…) не используются.
  Одноимённые файлы из разных папок: одинаковые — молча схлопываются, разные (2K/4K, цвета) —
  берётся первый вариант, в анализе — предупреждение, какую папку дропнуть отдельно.
* **Конфиги vs текст** — по содержимому, а не по расширению: `.meta/.xml/.txt`, у которого
  корневой XML-тег — игровой data-файл (`CWeaponInfoBlob`, `CWeaponComponentInfoBlob`,
  `content.xml`, `setup2.xml`…), — это конфиг мода, он уходит в пак **как есть** (побайтно;
  `weapons.meta.txt` → `weapons.meta`), шаблоны для этого слота не генерируются. Readme,
  инструкции, `assembly.xml` из OIV — пропускаются. Кусок конфига без корня («добавьте в
  weapons.meta…») — пропускается с предупреждением: установить его «как есть» нельзя.
* **Конфиги Replace-мода** — `weapons.meta`/`weaponcomponents.meta`/`weaponanimations.meta`/
  `weaponarchetypes.meta`, где всё объявленное — ванильное (`WEAPON_PISTOL`, …), — не ставятся:
  они переопределили бы стоковый ствол. Для add-on генерируются свои meta.
* **Готовый `dlc.rpf`** (RPF с `setup2.xml` в корне) где угодно внутри — побеждает
  россыпь моделей (Add-On + Replace версии в одном архиве). Прочие `.rpf` пропускаются.
* Имя оружия и имя пака берутся из имени архива/папки без мусора:
  `Glock_17_[4K]_v1.2.zip` → «Glock 17».

## Маршруты сборки (как в оригинале)

| Во входной папке | Что происходит |
|---|---|
| только `.ydr/.ytd` | базовое оружие по шаблону → генерируется весь стек meta, модели переименовываются в неймспейс `_awXXXXXX` |
| модели + свои `.meta/.xml` | свои файлы идут как есть (классификация по корневому тегу), недостающие генерируются с согласованными именами; модели не переименовываются |
| готовый `dlc.rpf` | ставится как есть, либо (общий пак) распаковывается и вливается в `AddonWeapons` |

После упаковки каждый `dlc.rpf` проходит самопроверку: каждый RSC7-ресурс должен
распаковываться ровно в размер страниц из флагов (ловит двойное сжатие).

## Проверка переноса

* **Паритет с Python** — `tools/parity/parity_check.py` гоняет 17 одинаковых
  сценариев через оригинальный `build_addon` и через `awbctl`: loose/packed,
  fallback по классу, свои meta, готовый `dlc.rpf`, ресурс > 16 МБ, кириллическое
  имя, 4 последовательных merge в общий пак (включая импорт dlc.rpf), установка в
  игру с `update.rpf` папкой и архивом. Сравниваются все meta побайтно, все записи
  RPF (порядок TOC, имена, флаги, распакованные данные), manifest-ы, `dlclist.xml`
  и строки лога — **всё совпадает**.
* `awbctl build-templates` генерирует библиотеку шаблонов, **идентичную** Python
  (133 JSON, включая `raw_xml`).
* Архивы C#-версии проходят Python-валидатор и наоборот; C#-валидатор находит
  двойное сжатие в `dlc-problem.rpf`.
* xUnit-тесты: GXT2, RPF (round-trip, двойное сжатие, большие записи, патч на
  месте, шифрованные архивы), имена, сканер, meta, dlclist, общий пак (переполнение
  3 ГБ-лимита, восстановление из вшитого `_pack.json`), валидация формы и сквозная
  сборка через команду ViewModel; drop игрока — вложенные папки, solid-7z с zip внутри,
  запароленный/битый архив, конфиги под `.txt`, фрагменты, Replace-конфиги, варианты,
  готовый `dlc.rpf` в архиве, сквозная сборка + установка из zip.

Совместимость с данными Python-версии сохранена: тот же `%LOCALAPPDATA%\AddonWeaponsBuilder`
(staging общего пака, логи), тот же формат `_pack.json`/`manifest.json`, тот же AppId установщика.

## Отличия от Python-версии

* RPF пишется потоково (данные → потом TOC): пак до 3 ГБ не держится в памяти
  целиком; `update.rpf` патчится на месте, без чтения всего архива в память;
  вложенные архивы читаются «окном» родительского файла, без временных файлов.
* Импортированные из готового `dlc.rpf` meta пишутся с нормальным CRLF (Python
  из-за двойной трансляции писал `\r\r\n`).
* Защита от тихой порчи архива: имя файла вне Latin-1 и таблица имён > 64 КБ дают
  понятную ошибку вместо битого TOC.
* Размеры `dlc.rpf` на байты отличаются — другая реализация DEFLATE (содержимое то же).
* Лог сборки виден всегда (раньше — только при ошибке); анализ источника до сборки.
* Режим игрока: drop-зона (папка или архив zip/rar/7z) вместо выбора папки с оружием.
* Не переносилось то, что было нужно только Python-стеку: проверки WebView2 /
  .NET Framework, снятие Mark-of-the-Web, Nuitka-скрипты, собственный Python-установщик
  (`awb_installer.py`). Их заменяют self-contained публикация и Inno Setup.

## Лог и диагностика

* Лог: `%LOCALAPPDATA%\AddonWeaponsBuilder\logs\awb.log` (кнопка «Log file» в окне).
* `AddonWeaponsBuilder.exe --diagnose` — отчёт о системе и целостности `data/`.
* Любой сбой старта показывает MessageBox и пишется в лог.

## Известные ограничения (унаследованы)

* Поддерживаются только незашифрованные RPF7 (OPEN — GTA V Legacy / OpenIV).
* Библиотека шаблонов — ванильные стволы; для DLC-оружия без точного шаблона
  используется структурный шаблон класса.
* Финальную загрузку пака стоит проверить в игре.
