# Разработка WinFlash

## Сборка

Нужно: Windows 10/11, [.NET SDK](https://dotnet.microsoft.com/download) 8 или новее (проект собирается под
.NET Framework 4.8 через пакет `Microsoft.NETFramework.ReferenceAssemblies`).

```powershell
.\build.ps1                       # Release-сборка в dist\WinFlash
.\build.ps1 -Configuration Debug  # отладочная сборка
.\build.ps1 -Flash E:\            # сборка и копирование на флешку Ventoy
```

`-Flash` обновляет на флешке программу, каталоги, скрипты и шаблон, кладёт копию `WinFlash.exe` в корень
и создаёт `autorun.inf` со значком. Не трогает `WinFlash\config\` (выбор пользователя и его свой шаблон),
`WinFlash\Apps\` (установщики) и пресеты `profiles\custom-*.json`.

Автотестов нет. Проверка — в виртуальной машине (см. ниже) и через UI Automation: у всех кнопок и галочек
уникальные `AutomationProperties.Name` / `AutomationId`, это нужно и экранному диктору.

## Устройство проекта

| Путь | Что там |
|---|---|
| `src\WinFlash\App.xaml.cs` | Точка входа: конфигуратор, `--apply` (окно применения), поиск папки набора |
| `src\WinFlash\Views\MainWindow.*` | Главное окно: шаги, пресеты, предпросмотр, мини-пресеты вкладок |
| `src\WinFlash\Views\ViewModels.cs` | Логика галочек, «Свой режим», уровни «Оптимально / Максимум», выбор Windows |
| `src\WinFlash\Views\ApplyWindow.*` | Окно «Настраиваем Windows» при первом входе |
| `src\WinFlash\Views\Theme.xaml`, `Icons.xaml` | Тема и векторные иконки (Fluent UI System Icons, MIT) |
| `src\WinFlash\Core\Deployer.cs` | Запись выбора на флешку: `selected.json`, файл автоустановки, `ventoy.json` |
| `src\WinFlash\Core\WinPeSetup.cs` | Режим WinPE: подключение ISO и запуск `setup.exe /unattend` |
| `src\WinFlash\Core\PresetCode.cs` | Коды пресетов `WF-…` (старые `RK-…` тоже читаются) |
| `kit\catalog\tweaks.json` | Настройки: реестр, задачи планировщика, `icon`, `level`, `win` |
| `kit\catalog\apps.json` | Программы для установки (winget, офлайн-маска) и встроенные приложения для удаления |
| `kit\profiles\*.json` | Пресеты «Стандарт», «Производительность», «Для игр» |
| `kit\icons\<id>.png` | Значки программ и приложений 64×64 |
| `kit\scripts\Apply-Profile.ps1` | Применение профиля на установленной Windows (с журналом отката) |
| `kit\scripts\Undo-Profile.ps1` | Откат по журналу |
| `kit\unattend\base.xml` | Нейтральный шаблон автоустановки |
| `winpe\startnet.cmd` | Скрипт запуска загрузочного образа |
| `tools\Organize-Flash.ps1` | Раскладка флешки по папкам и названия в меню Ventoy |

### Каталоги

**Новые элементы добавляйте только в конец списков** `tweaks`, `install`, `remove`: коды пресетов хранят
порядковые номера, и вставка в середину изменит смысл старых кодов.

Поля элемента:
- `level`: `optimal` — входит в «Оптимально» и «Максимум»; `max` — только в «Максимум»; без поля — ни туда, ни туда;
- `win`: `10` или `11`, если пункт нужен только в этой версии (в другой он скрывается и не применяется);
- `icon`: имя иконки из `Views\Icons.xaml` без `ic.` (для настроек); значки программ — файл `kit\icons\<id>.png`;
- `tasks` (настройки): задачи планировщика, которые отключаются (откат включает их обратно).

Пресеты собираются из уровней: «Стандарт» = все `optimal`; «Производительность» = + `max` из «Рекламы и слежки»,
удаления и раздела «Производительность»; «Для игр» = + все `max` оптимизации.

### Режимы запуска

| Команда | Что делает |
|---|---|
| `WinFlash.exe` | Конфигуратор |
| `WinFlash.exe --apply` | Применить `config\selected.json` (запускается автоматически при первом входе) |
| `WinFlash.exe --apply --force` | Применить ещё раз, даже если уже применялось (`config\applied.txt`) |
| `WinFlash.exe --kit <папка>` | Взять каталоги и скрипты из другой папки |

Скрипт применения можно проверить без изменений системы:

```powershell
.\kit\scripts\Apply-Profile.ps1 -ProfilePath test.json -KitRoot .\kit -LogDir $env:TEMP\wf-log -WhatIf
```

## Загрузочный образ (WinFlash-Setup.iso)

Образ не содержит программу: `startnet.cmd` ищет на всех дисках `WinFlash\WinFlash.exe` (затем старое
`ReinstallKit\ReinstallKit.exe`) и запускает его. Поэтому после обновления программы образ пересобирать не нужно.

Сборка (нужны [Windows ADK и надстройка WinPE](https://learn.microsoft.com/windows-hardware/get-started/adk-install), права администратора):

```bat
copype amd64 C:\winpe
dism /Mount-Image /ImageFile:C:\winpe\media\sources\boot.wim /Index:1 /MountDir:C:\winpe\mount
```

Добавьте пакеты из `...\Windows Preinstallation Environment\amd64\WinPE_OCs` (и их `en-us`-версии)
командой `dism /Add-Package /Image:C:\winpe\mount /PackagePath:<файл.cab>`, в таком порядке:
`WinPE-WMI`, `WinPE-NetFx`, `WinPE-Scripting`, `WinPE-PowerShell`, `WinPE-StorageWMI`, `WinPE-DismCmdlets`,
`WinPE-FontSupport-WinRE` (шрифт Segoe UI для кириллицы). Затем:

```bat
copy winpe\startnet.cmd C:\winpe\mount\Windows\System32\startnet.cmd
dism /Unmount-Image /MountDir:C:\winpe\mount /Commit
del C:\winpe\media\Boot\bootfix.bin
oscdimg -m -o -u2 -udfver102 -lWINFLASH -bootdata:2#p0,e,bC:\winpe\fwfiles\etfsboot.com#pEF,e,bC:\winpe\fwfiles\efisys_noprompt.bin C:\winpe\media WinFlash-Setup.iso
```

`efisys_noprompt.bin` и удаление `bootfix.bin` убирают приглашение «Press any key to boot from CD».

## Проверка в виртуальной машине

VirtualBox на ПК с включённой «Целостностью памяти» (HVCI) работает через Hyper-V; для гостевой Windows нужны
`--cpus 2 --paravirt-provider minimal --large-pages off --tpm-type none`.

- **WinPE:** ВМ с `WinFlash-Setup.iso` и виртуальным диском, на котором лежат `WinFlash\` и `ISO\Windows\*.iso`.
- **Полная установка:** ВМ с образом Windows и маленьким ISO, в корне которого `autounattend.xml`
  (из `config\autounattend.xml` при записи без Ventoy) и папка `WinFlash\` с `config\selected.json`.

Не используйте для проверок настоящие настройки, ключи и данные: только тестовые копии.

## Выпуск новой версии

1. Поднимите версию в `src\WinFlash\WinFlash.csproj` (`<Version>X.Y.Z</Version>`).
2. Соберите: `.\build.ps1`.
3. Упакуйте программу так, чтобы в архиве были `WinFlash\` и `WinFlash.exe` в корне:
   ```powershell
   $v = 'X.Y.Z'; $t = "$env:TEMP\wf-release"
   Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue
   New-Item -ItemType Directory "$t\ventoy\script" | Out-Null
   Copy-Item dist\WinFlash "$t\WinFlash" -Recurse
   Copy-Item dist\WinFlash\WinFlash.exe $t
   Set-Content "$t\ventoy\script\README.txt" 'Сюда WinFlash запишет файл автоустановки winflash.xml.'
   Compress-Archive "$t\*" "WinFlash-$v.zip" -Force
   Get-FileHash "WinFlash-$v.zip", WinFlash-Setup.iso -Algorithm SHA256
   ```
   Папка `ventoy` нужна, чтобы программа узнала флешку Ventoy (Ventoy сам её не создаёт).
4. Напишите описание по шаблону `docs/RELEASE_NOTES_TEMPLATE.md`.
5. Создайте выпуск (тег совпадает с версией в csproj):
   ```powershell
   gh release create vX.Y.Z "WinFlash-X.Y.Z.zip" "WinFlash-Setup.iso" --title "WinFlash X.Y.Z" --notes-file docs/release-notes-draft.md
   ```
6. Проверьте страницу выпуска: оба файла скачиваются, плашка версии в README обновилась.

## Как писать описание релиза

1. Пишем для пользователя, а не для разработчика: не «рефакторинг Deployer», а «Флешка сама убирает старые записи из меню загрузки».
2. Заголовок: `## WinFlash X.Y.Z · ГГГГ-ММ-ДД`.
3. Первая строка — одно предложение о главном в версии.
4. Разделы «Новое», «Улучшено», «Исправлено», «Важно» — только непустые. «Важно» — если нужно что-то сделать вручную.
5. Один пункт — одно изменение, начинается с действия или результата, не длиннее двух строк.
6. Ошибки описываем по симптому, который видел пользователь, а не по причине в коде.
7. Если поведение изменилось — что именно и где это настраивается.
8. Предупреждаем о том, что ломает привычное: переименования, сброс настроек, переезд файлов.
9. Внизу: файлы выпуска, SHA-256, ссылка «Полный список изменений».
10. Версия по semver; тег `vX.Y.Z` совпадает с версией в csproj.
11. Одинаковая структура в каждом релизе.
12. Не обещаем того, чего нет: никаких «улучшена производительность» без конкретики.

## История

Проект начинался под названием **ReinstallKit**. В версии 0.2.0 переименован в WinFlash: папка на флешке,
программа, образ и шаблон автоустановки называются `WinFlash`, коды пресетов начинаются с `WF-`.
Загрузочный образ по-прежнему находит старую папку `ReinstallKit`, а программа принимает коды `RK-`.

В публичной версии шаблон автоустановки нейтральный: он не отключает защиту Windows, не включает удалённый
рабочий стол и не удаляет приложения сам — удаление идёт только по галочкам.
