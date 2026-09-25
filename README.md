# AddonWeapons Builder (C# / Avalonia)

Превращает **replace-сборку** оружия для GTA V (модели/текстуры, названные под
существующее оружие) в **add-on DLC-пак**, который детектится нативами
`GET_NUM_DLC_WEAPONS` / `GET_DLC_WEAPON_DATA` и появляется в меню скрипта
AddonWeapons — без замены ванильных стволов. Работает с **GTA V Legacy и GTA V
Enhanced** и умеет сразу установить пак в игру через папку `mods` — в том числе в
«чистую» игру без OpenIV (см. «Legacy и Enhanced»).

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
    Rpf/                     RPF7 writer/reader/verify/patch            ← rpf.py
      ResourceEditions.cs    Legacy → Enhanced (gen9) конвертация моделей через CodeWalker (новое)
      GameCrypto.cs          NG/AES-ключи из GTA5.exe / GTA5_Enhanced.exe для игровых архивов (новое)
    GameEdition.cs           Legacy / Enhanced: определение по exe (новое)
    DlcAssembler.cs          дерево dlcpack → dlc.rpf                   ← assembler.py
    MergedPack.cs            общий пак AddonWeapons[N], лимит 3 ГБ      ← merge.py
    GameInstaller.cs         подготовка игры (плагин, mods, update.rpf) + установка + dlclist.xml ← installer.py
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
  plugins/                   OpenIV.asi (Legacy), RageOpenV.asi (Enhanced) — ставятся в «чистую» игру
external/CodeWalker/         git submodule (dexyfex/CodeWalker) — используется только CodeWalker.Core
tests/Awb.Tests/             xUnit-тесты ядра и ViewModel
tools/parity/                сверка с Python-оригиналом
tools/Awb.UiSnapshot/        офскрин-рендер окна в PNG (Avalonia.Headless)
installer/                   Inno Setup (тот же AppId, что у Python-версии)
build.ps1                    тесты + self-contained публикация (+ zip / установщик)
```

## Сборка и запуск

Нужен .NET SDK 10. CodeWalker подключён сабмодулем — клонировать с `--recursive`
(или после клона: `git submodule update --init`).

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
    [--model-name w_pi_mygun] [--no-pack] [--merge-pack] [--install-game-dir "D:\GTA V"] \
    [--edition legacy|enhanced|auto]   # auto: по exe в --install-game-dir, иначе legacy
awbctl verify <archive.rpf>        # самопроверка ресурсов готового архива (новое)
```

## Legacy и Enhanced

Контейнер RPF7 у обеих версий один и тот же: моды в папке `mods` — OPEN-архивы (CodeWalker
для Gen9 пробовал `NONE` и вернулся к `OPEN`). Различаются **ресурсы внутри**: у Enhanced
(gen9) свои версии и раскладка блоков — `.ydr/.ydd` v159 вместо 165, `.ytd` v5 вместо 13,
`.yft` v171 вместо 162.

* **Выбор версии.** В режиме игрока — автоматически по exe в папке игры (`GTA5.exe` →
  Legacy, `GTA5_Enhanced.exe` → Enhanced), переключатель «Game version» можно поправить
  вручную. В режиме моддера — переключатель (запоминается). CLI — `--edition`.
* **Конвертация моделей.** Для Enhanced Legacy-модели конвертируются в gen9 при упаковке
  (CodeWalker.Core, тот же код, что в CodeWalker Gen9 Converter), результат проверяется:
  версия gen9 и страницы ровно по флагам. Уже gen9-модели не трогаются. Работает во всех
  маршрутах: генерация, свои meta, loose-папки, общий пак, готовый `dlc.rpf` (перепаковывается,
  всё кроме моделей — байт в байт). Обратной конвертации нет: gen9-модель в Legacy-пак —
  понятная ошибка.
* **Общий пак** хранится отдельно для каждой версии (`staging` — Legacy, как раньше,
  `staging-enhanced` — Enhanced); исходные модели в staging не конвертируются.

### «Чистая» игра

Если в папке игры нет ни одного из `OpenIV.asi`, `DSOUND.dll`, `OpenRPF.asi`,
`RageOpenV.asi`, при установке:

1. копируется плагин из `data/plugins`: `OpenIV.asi` для Legacy, `RageOpenV.asi` для Enhanced;
   если нет ASI-лоадера (`dinput8.dll`, `xinput1_4.dll`, …) — предупреждение в логе
   (для Legacy нужен `dinput8.dll`, для Enhanced — `xinput1_4.dll`: ScriptHookV или Ultimate ASI Loader);
2. создаётся папка `mods`;
3. `update\update.rpf` копируется в `mods\update\update.rpf` (2–3 ГБ, только первый раз,
   с проверкой свободного места);
4. при первой правке `dlclist.xml` скопированный архив переводится из игрового NG-шифрования
   в OPEN — как это делают OpenIV/CodeWalker: TOC расшифровывается ключами, найденными в
   `GTA5.exe` / `GTA5_Enhanced.exe` (по SHA1, как в CodeWalker; ключей в программе нет),
   остальные записи остаются как есть. Если новый `dlclist.xml` не влезает в свои секторы,
   запись переносится в конец архива.

Если `mods\update\update.rpf` — распакованная папка без `dlclist.xml`, он берётся из
игрового `update.rpf`.

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

## Известные ограничения

* Собираемые архивы — OPEN; зашифрованные игрой (NG/AES) архивы читаются только при
  установке (нужен exe игры в той же папке).
* Конвертация Enhanced → Legacy невозможна (её нет и в CodeWalker).
* Библиотека шаблонов — ванильные стволы; для DLC-оружия без точного шаблона
  используется структурный шаблон класса.
* Финальную загрузку пака стоит проверить в игре.
