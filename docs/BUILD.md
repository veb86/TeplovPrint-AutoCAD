# Сборка и выпуск

## Требования

- Windows;
- .NET SDK, указанный в `global.json`;
- восстановленные NuGet-пакеты (reference assemblies AutoCAD берутся из пакетов `AutoCAD.NET`/`AutoCAD.NET.Core`, nanoCAD 24 — из `ModPlus.nanoCAD.API.24`, установленный CAD для сборки не нужен).

## Сборка

Собрать всё решение:

```powershell
dotnet build LA.BatchPlot.sln
```

Собрать конкретную целевую версию:

```powershell
.\scripts\build-dll.ps1 -Target AutoCAD -Configuration Release
.\scripts\build-dll.ps1 -Target AutoCAD2025 -Configuration Release
.\scripts\build-dll.ps1 -Target NanoCAD24 -Configuration Release
```

Для всех сборок используйте `-Target All`. Скрипт не создаёт альтернативные каталоги: результаты всегда попадают в `bin-acad`, `bin-acad2025-2027` и `bin-nanocad24`.

Сборка nanoCAD 24 (`src/NcadBatchPlot`, .NET 6) компилирует те же исходники с символами `AUTOCAD;NANOCAD`: пространства имён `Autodesk.AutoCAD.*` заменяются на `HostMgd.*`/`Teigha.*` в ветках `#if NANOCAD`. Ветка `NANOCAD` должна проверяться раньше `AUTOCAD` и `ACAD_CORE`.

Ограничения nanoCAD 24, которые нужно учитывать в коде:

- часть членов `hostmgd.dll` в nanoCAD 24 — заглушки: вместо работы они показывают модальное окно «Mgd / NOT_IMPLEMENTED / <член>» (например, `Application.MenuBar`, `Application.MenuGroups`, `PlotConfigManager.RefreshList`, `PlotInfoValidator.Validate`). Такие вызовы исключаются из сборки nanoCAD через `#if !NANOCAD`;
- `NETLOAD` загружает плагин через `Assembly.LoadFile` в отдельный `AssemblyLoadContext` и вызывает `Initialize()` для каждой новой загруженной копии сборки. Поэтому `AssemblyVersion` должна быть четырёхкомпонентной (`1.0.0.0`): WPF ищет загруженную сборку по версии из pack URI XAML, и при `1.0.0` загружалась вторая копия DLL («отсутствует ресурс, соответствующий uri …»), а `PluginAssemblyResolver` никогда не загружает уже загруженную сборку повторно.
- плоттеры комплекта `LA_*.pc3`/`LA_*.pmp` — шаблоны AutoCAD с драйверами Autodesk (`pdfplot16.hdi` и др.); при установке путь драйвера копируется из штатного `DWG To PDF.pc3` AutoCAD. В nanoCAD такого плоттера нет, поэтому сборка nanoCAD не создаёт `LA_*` (`AcadPlotterInstaller.UsesBundledLaPlotters == false`), а печатает через собственные плоттеры nanoCAD, выбранные `PlotDeviceSelector` из `PlotSettingsValidator.GetPlotDeviceList()` (сначала встроенный PDF-плоттер; переопределяется переменной окружения `TEPLOV_PRINT_PDF_DEVICE`). Пользовательская бумага в nanoCAD не регистрируется: используется ближайший формат плоттера.
- в issue #7 nanoCAD 24 отклонил все попытки настроить через `PlotSettingsValidator` объект `PlotSettings` вне базы чертежа (`new PlotSettings(...)` для `PlotInfo.OverrideSettings`) исключением `SEHException` «External component has thrown an exception»: обёртки `hostdbmgd.dll` перехватывают только `OdError`, остальные нативные исключения приходят без описания. В сборке nanoCAD `OutputWithJobSettings` в этом случае применяет параметры к самому листу (`OutputWithConfiguredLayout`), печатает его без `OverrideSettings` и восстанавливает прежние параметры листа. Каждый шаг `ConfigurePlotSettings` выполняется через `PlotStepException.Run`, поэтому в ошибке видно, какой вызов отклонён.

## Тесты

```powershell
dotnet test tests/NcadBatchPlot.Tests
```

Тесты собирают `bin-nanocad24/NcadBatchPlot.dll` и проверяют его без nanoCAD:

- `NanoCadApiUsageTests` находит заглушки в настоящих `hostmgd.dll`/`hostdbmgd.dll` nanoCAD 24 (пакет `ModPlus.nanoCAD.API.24`) и падает, если плагин ссылается хотя бы на одну из них;
- `NetloadXamlResourceTests` повторяет загрузку `NETLOAD` (`Assembly.LoadFile` и обработчик `AssemblyResolve` nanoCAD) и поиск сборки WPF для pack URI окна `BatchPlotForm` и проверяет, что вторая копия плагина не загружается.
- `NanoCadPdfPlotterTests` строит граф вызовов IL плагина и падает, если какой-либо путь вызова в сборке nanoCAD ведёт к пересборке `LA_*.pc3` из драйвера AutoCAD (`TryWriteLaPia2PairFromTemplate`), а также проверяет выбор PDF/DWF-плоттера nanoCAD (`PlotDeviceSelector`).
- `NanoCadPlotSettingsTests` проверяет по графу вызовов, что печать и предварительный просмотр nanoCAD восстанавливают параметры печати листа, и что ошибки шагов настройки называют шаг и тип исключения (`PlotStepException`).
- `NanoCadPrintRouteTests` проверяет, что nanoCAD не печатает через пустой управляемый `PlotEngine` и не переписывает лист через `PlotSettingsValidator` (закрытие nanoCAD в 1.0.3, issue #9), а печатает через COM-объект `Plot`; также проверяются масштаб для COM-листа и поиск файла печати под другим расширением.
- `PrintSessionLogTests` проверяет журнал `%TEMP%\TeplovPrint-print.log`: он создаётся при каждом «Начать печать», заменяет прежний, сохраняет каждую строку сразу и содержит окружение, шаги и ошибки.

Диагностика загрузки в nanoCAD включается переменной окружения `TEPLOV_PRINT_TRACE=1` (задать до запуска CAD): журнал пишется в `%TEMP%\TeplovPrint-trace.log`. В журнал также записываются список устройств печати nanoCAD и выбранный PDF-плоттер. Журнал печати `%TEMP%\TeplovPrint-print.log` пишется всегда, без переменной окружения: он создаётся заново при каждом «Начать печать», и пока печать идёт, туда же попадают сообщения трассировки.

## Пакет выпуска

`scripts/package-release.ps1` формирует локальный выпуск из трёх папок сборки. Перед запуском соберите все DLL (`-Target All`):

```powershell
.\scripts\package-release.ps1 -Version 1.0.7
.\scripts\test-release-package.ps1 -Version 1.0.7
```

Архивы создаются в папке `release` и не входят в исходный код:

- `Teplov-Print-AutoCAD2015-2024-v<версия>.zip`, `Teplov-Print-AutoCAD2025-2027-v<версия>.zip`, `Teplov-Print-nanoCAD24-v<версия>.zip` — содержимое папок сборки;
- `Teplov-Print-manual-install-v<версия>.zip` — ручная установка в профиль пользователя: структура архива повторяет `%USERPROFILE%` (`AppData\Roaming\...`), внутри `INSTALL.md` из `installer_manual`, `install.cmd` и `uninstall.cmd`.

`test-release-package.ps1` проверяет состав архивов (наличие DLL и зависимостей, соответствие `PackageContents.xml`, отсутствие `.pdb` и чужих `runtimes`).

## Автоматическая сборка и выпуск

Workflow `.github/workflows/build.yml` на каждый push и pull request собирает все DLL на `windows-latest`, запускает `tests/NcadBatchPlot.Tests`, формирует и проверяет архивы и прикладывает к запуску артефакты `TeplovPrint-AutoCAD2021-dll`, `TeplovPrint-nanoCAD24-dll` и `release-archives`.

Выпуск GitHub `v<версия>` с архивами публикуется автоматически при push в основную ветку, если выпуска с текущей `<Version>` ещё нет, при push тега `v<версия>` или при ручном запуске workflow. Чтобы выпустить новую версию, измените `<Version>` (и связанные `AssemblyVersion`/`FileVersion`) во всех `.csproj`.
