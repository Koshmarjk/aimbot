# HachBob AI — C# / .NET 8 / WPF

Полностью нативная реализация на C# без зависимостей от Python/pip.
## Модели для детекции
https://drive.google.com/drive/folders/1iWcrr-2MuxFeKV1Sk2UKpiGuny-_qBMF?usp=sharing

## Требования

- Windows 10/11 x64
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8
- Для NVIDIA: CUDA Toolkit + cuDNN + TensorRT (опционально)
- Для AMD/Intel: ничего лишнего — DirectML встроен в Windows

## Быстрый старт

```bash
git clone ...
cd HachBobAI
dotnet run -c Release
```

## Сборка в exe

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o ./dist
```

## Структура

```
HachBobAI/
├── Vision/
│   ├── VisionEngine.cs          — ONNX инференс + пайплайн обнаружения
│   └── ScreenCaptureDXGI.cs     — DXGI Desktop Duplication (~0.5ms) + GDI fallback
├── Input/
│   └── MouseLogic.cs            — SendInput + EMA smoothing + тригербот
├── Config/
│   └── AppConfig.cs             — JSON конфиг + пресеты
├── Audio/
│   └── SoundManager.cs          — NAudio
├── UI/
│   ├── Windows.cs               — Overlay + Indicator
│   ├── SliderRow.xaml/.cs       — Компонент слайдера
│   ├── BindsPanel.xaml/.cs      — Настройка биндов
│   ├── DeadZonesPanel.xaml/.cs  — Мёртвые зоны
│   └── PresetsPanel.xaml/.cs    — Пресеты оружий
├── MainWindow.xaml/.cs          — Главное окно
└── HachBobAI.csproj
```

## Производительность

| Компонент       | Метод                          | Латентность |
|-----------------|--------------------------------|-------------|
| Захват экрана   | DXGI Desktop Duplication       | ~0.5–1 мс  |
| Захват экрана   | GDI BitBlt (fallback)          | ~5–15 мс   |
| Ввод мыши       | SendInput (атомарный вызов)    | <1 мс      |
| Инференс NVIDIA | ONNX + TensorRT                | ~2–5 мс    |
| Инференс NVIDIA | ONNX + CUDA                    | ~3–8 мс    |
| Инференс AMD    | ONNX + DirectML                | ~5–15 мс   |

## Файлы (рядом с exe)

```
model.onnx      — YOLOv8 person-only модель
config.json     — создаётся автоматически
presets.json    — создаётся автоматически
sounds/
  on.mp3
  off.mp3
  gui_on.mp3
  gui_off.mp3
```

## Горячие клавиши (по умолчанию)

| Клавиша | Действие          |
|---------|-------------------|
| X2 (MB5)| Удержать аим      |
| X1 (MB4)| Сменить цель      |
| INSERT  | Вкл / Выкл        |
| HOME    | Скрыть GUI        |
| V       | Dead zones вкл    |
| \\      | Тригербот вкл     |
| F4      | Overlay           |
| F12     | Выход             |
| F1–F5   | Пресеты оружий    |
