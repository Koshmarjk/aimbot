# 🎯 HachBob AI — C# / .NET 8 / WPF

Нативная реализация на C# без зависимостей от Python.

---

# 👤 ДЛЯ ПОЛЬЗОВАТЕЛЕЙ (готовый билд)

## 📦 Версии

В архиве 3 папки. Выбери ту что подходит твоему железу:

| Папка | Для кого | Размер | Файлов |
|-------|----------|--------|--------|
| `NVIDIA\` | NVIDIA RTX/GTX с CUDA | ~400 МБ | 1-5 |
| `DML\`    | Любые видеокарты (AMD/Intel/NVIDIA) | ~50 МБ | ~30 |
| `OpenVINO\` | Intel CPU / iGPU / NPU | ~100 МБ | 1-5 |

### Какую выбрать?

| Твоё железо | Запускай |
|-------------|----------|
| NVIDIA RTX 20xx/30xx/40xx | `NVIDIA\` (макс. FPS) |
| NVIDIA GTX 16xx/10xx | `NVIDIA\` или `DML\` |
| AMD Radeon (любая) | `DML\` |
| Intel Arc / Iris Xe | `DML\` или `OpenVINO\` |
| Только встроенная Intel HD | `OpenVINO\` |
| Не знаешь что у тебя | `DML\` — работает на всём |

---

## 🔧 ОБЯЗАТЕЛЬНО — установить .NET 8 Desktop Runtime

Без него программа не запустится.

**Скачать:** https://dotnet.microsoft.com/download/dotnet/8.0

Выбрать: **".NET Desktop Runtime 8.0.x"** → **Windows x64 Installer**

Установить, перезагрузка не нужна.

---

## 🎮 Дополнительно для NVIDIA версии

Только если используешь папку `NVIDIA\`:

1. **Свежий драйвер NVIDIA:** https://www.nvidia.com/Download/index.aspx
2. **CUDA Toolkit 12.x:** https://developer.nvidia.com/cuda-downloads
3. *(Опционально, для макс. FPS)* **TensorRT 10.x:** https://developer.nvidia.com/tensorrt
   Распаковать, добавить `TensorRT/lib` в PATH

После установки CUDA — перезагрузить ПК.

**Для DML и OpenVINO** ничего дополнительно ставить не нужно — всё встроено.

---

## 🚀 Первый запуск

1. Распакуй архив (например в `C:\HachBobAI\`)
2. Зайди в нужную папку (`NVIDIA\`, `DML\` или `OpenVINO\`)
3. Запусти `HachBobAI.exe`
4. Если Windows ругается через SmartScreen — нажми "Подробнее → Выполнить в любом случае"
5. Через UI или `config.json` укажи путь к своей модели `.onnx`

---

## ⚠ Если не работает

| Проблема | Решение |
|----------|---------|
| Окно не появляется | Не установлен .NET 8 Desktop Runtime |
| FPS = 0, в индикаторе [CPU] | Обнови драйвер видеокарты, попробуй другую версию |
| Крашится при выборе .onnx | Модель использует opset который не поддерживается — нужен opset 12 или ниже |
| Single-file долго стартует | Первый запуск распаковывает DLL в `%TEMP%\.net\`, второй раз быстрее |

---

## ⌨ Горячие клавиши (по умолчанию)

| Клавиша | Действие          |
|---------|-------------------|
| LMB (MB1)| Удержать аим      |
| X1 (MB4)| Сменить цель      |
| INSERT  | Вкл / Выкл        |
| HOME    | Скрыть GUI        |
| V       | Dead zones вкл    |
| \       | Тригербот вкл     |
| DEL      | Overlay           |
| F12     | Выход             |
| F1–F8   | Пресеты оружий    |

---

## 📁 Файлы рядом с exe
HachBobAI.exe — основной файл
config.json — настройки (создаётся автоматически)
presets.json — пресеты (создаётся автоматически)

Модель `.onnx` указывай через UI программы — она может лежать где угодно.

---

---

# 🛠 ДЛЯ РАЗРАБОТЧИКОВ

## Требования

- Windows 10/11 x64
- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8
- Visual Studio 2022 или VS Code
- Для NVIDIA сборки: CUDA Toolkit + cuDNN + TensorRT (опционально)

## Быстрый старт

```bash
git clone ...
cd HachBobAI
dotnet run -c Release_DML
Сборка всех версий
Bash

build_all.bat
Создаст 3 папки в ReleaseBuilds\:

NVIDIA\ — single-file для NVIDIA (CUDA + TensorRT)
DML\ — распакованная для DirectML (single-file ломает DML)
OpenVINO\ — single-file для Intel
Сборка отдельной версии вручную
Bash

# NVIDIA (single-file)
dotnet publish -c Release_NVIDIA -r win-x64 --self-contained false -p:PublishSingleFile=true -o ReleaseBuilds\NVIDIA

# DML (распакованная — single-file ломает DirectML EP)
dotnet publish -c Release_DML -r win-x64 --self-contained false -o ReleaseBuilds\DML

# OpenVINO (single-file)
dotnet publish -c Release_OpenVINO -r win-x64 --self-contained false -p:PublishSingleFile=true -o ReleaseBuilds\OpenVINO
Флаг --self-contained false означает что пользователю нужен установленный .NET 8 Desktop Runtime. Это экономит ~250 МБ на каждую сборку.

Конфигурации проекта
Конфигурация	Описание	Пакеты ONNX
Release_NVIDIA	CUDA + TensorRT	Microsoft.ML.OnnxRuntime.Gpu
Release_DML	DirectML	Microsoft.ML.OnnxRuntime.DirectML
Release_OpenVINO	OpenVINO Native + ONNX CPU	Microsoft.ML.OnnxRuntime + OpenVINO.CSharp.API
Debug / Release	Fallback для IDE (использует DML)	Microsoft.ML.OnnxRuntime.DirectML
Условная компиляция
OpenVINO код обёрнут в #if USE_OPENVINO — компилируется только для Release_OpenVINO.
Это позволяет DML и NVIDIA сборкам не тянуть лишние ~93 МБ OpenVINO DLL.

Структура проекта
text

HachBobAI/
├── Vision/
│   ├── VisionEngine.cs          — ONNX инференс + пайплайн обнаружения
│   ├── OpenVinoSession.cs       — OpenVINO Native (только для Release_OpenVINO)
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
├── build_all.bat                — Сборка всех 3 версий
└── HachBobAI.csproj
Производительность
Компонент	Метод	Латентность
Захват экрана	DXGI Desktop Duplication	~0.5–1 мс
Захват экрана	GDI BitBlt (fallback)	~5–15 мс
Ввод мыши	SendInput (атомарный вызов)	<1 мс
Инференс NVIDIA	ONNX + TensorRT	~2–5 мс
Инференс NVIDIA	ONNX + CUDA	~3–8 мс
Инференс AMD/Intel	ONNX + DirectML	~5–15 мс
Инференс CPU	ONNX + OpenVINO Native	~10–25 мс
FPS бенчмарки (модель YOLOv8n 416×416, capture 640×640)
GPU	EP	FPS
RTX 4070 Ti	TensorRT FP16	800+
RTX 4070 Ti	DirectML	500+
AMD RX 6800	DirectML	400+
Intel Arc A750	DirectML	350+
Intel UHD 770	OpenVINO CPU	60-80
Известные ограничения
DirectML EP не поддерживает PublishSingleFile — DML сборка всегда распакованная (~30 файлов)
OpenVINO требует opset ≤ 17 для большинства моделей
DirectML требует opset ≤ 17 в onnxruntime 1.20.x
