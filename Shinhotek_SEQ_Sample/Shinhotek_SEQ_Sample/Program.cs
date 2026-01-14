using LumosityXMLInterface;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using static LumosityXMLInterface.XMLInterface;

namespace Shinhotek_SEQ_Sample
{
    internal class Program
    {
        static XMLInterface _xmlInterface = new XMLInterface();
        static int _frameCount = 3;

        static volatile int _currframeNumber = 0;

        // "새 프레임 / 평가 도착" 신호
        static readonly AutoResetEvent _frameArrived = new AutoResetEvent(false);

        // 측정 데이터
        static string _frameDate = string.Empty;
        static string _frameTime = string.Empty;
        static string _beamSizeLong = string.Empty;
        static string _beamSizeShort = string.Empty;

        static string _vertical1090LengthAscent_1 = string.Empty;
        static string _vertical1090LengthDescent_1 = string.Empty;
        static string _vertical1090LengthAscent_2 = string.Empty;
        static string _vertical1090LengthDescent_2 = string.Empty;
        static string _vertical1090LengthAscent_3 = string.Empty;
        static string _vertical1090LengthDescent_3 = string.Empty;
        static string _vertical1090LengthAscent_4 = string.Empty;
        static string _vertical1090LengthDescent_4 = string.Empty;
        static string _vertical1090LengthAscent_5 = string.Empty;
        static string _vertical1090LengthDescent_5 = string.Empty;

        static string _uniformity = string.Empty;
        static string _mean = string.Empty;
        static string _stdDev = string.Empty;

        static void Main(string[] args)
        {
            _xmlInterface = new XMLInterface();

            string confPath = GetArgValue(args, "--conf", "-c");

            string _outputPath = @"C:\LumosityData";
            if (!Directory.Exists(_outputPath))
                Directory.CreateDirectory(_outputPath);

            _xmlInterface.FrameEvaluations += OnFrameEvaluations;
            _xmlInterface.Disconnected += OnDisconnected;
            _xmlInterface.ErrorOccurred += OnErrorOccurred;

            while (true)
            {
                // 1. 연결
                Measure("[1/7] Lumosity 연결", () =>
                {
                    if (!_xmlInterface.Connect("127.0.0.1", 4096))
                        throw new Exception("연결 실패!");
                });
                Console.WriteLine("연결 성공!");
                Thread.Sleep(200);

                // 2. 장비 정보 조회
                Measure("[2/7] 장비 정보 조회", () =>
                {
                    if (!_xmlInterface.CmdGetInfomation())
                        throw new Exception("정보 조회 실패!");
                });
                DisplayDeviceInfo();
                Thread.Sleep(200);

                Measure("[3/7] Config 로드", () =>
                {
                    LoadConfigIfProvided(confPath);
                });

                // 3. 측정 항목 선택 및 기본 설정
                Measure("[4/7] 측정 항목 및 기본 설정", () =>
                {
                    _xmlInterface.ClearUseEvaluations();
                    _xmlInterface.AddUseEvaluation("FRAME_NUMBER");
                    _xmlInterface.AddUseEvaluation("FRAME_DATE");
                    _xmlInterface.AddUseEvaluation("FRAME_TIME");
                    _xmlInterface.AddUseEvaluation("FRAME_BEAMWIDTH_LONG");
                    _xmlInterface.AddUseEvaluation("FRAME_BEAMWIDTH_SHORT");
                    _xmlInterface.AddUseEvaluation("VERTICAL_1090_LENGTH_ASCENT");
                    _xmlInterface.AddUseEvaluation("VERTICAL_1090_LENGTH_DESCENT");
                    _xmlInterface.AddUseEvaluation("ROI2D_UNIFORMITY");
                    _xmlInterface.AddUseEvaluation("ROI2D_MEAN");
                    _xmlInterface.AddUseEvaluation("ROI2D_STDDEV");

                    _xmlInterface.BlurEnable = false;
                    _xmlInterface.FrameROIActive = false;
                    _xmlInterface.FrameBeamSectionActive = false;
                    _xmlInterface.FrameCrossSectionActive = true;
                    _xmlInterface.FrameCrossSectionAuto = false;
                    _xmlInterface.NumberRestrictionEnable = true;
                    _xmlInterface.NumberRestrictionValue = _frameCount;
                    _xmlInterface.IsEvaluationContinuous = true;
                });

                // 4. 초기 프레임 수신 및 취득 시작
                _currframeNumber = 0;

                Measure("[5/7] Start + 초기 프레임 수신", () =>
                {
                    _xmlInterface.Start();

                    int previousFrameNumber = -1;
                    var sw = Stopwatch.StartNew();

                    while (_currframeNumber < _frameCount)
                    {
                        if (previousFrameNumber != _currframeNumber)
                        {
                            Console.WriteLine($"  - 프레임 {_currframeNumber} 수신");
                            previousFrameNumber = _currframeNumber;
                        }

                        // 너무 타이트하게 돌지 않게
                        Thread.Sleep(10);

                        // 안전장치 (예: 10초)
                        if (sw.ElapsedMilliseconds > 10_000)
                            throw new TimeoutException("초기 프레임 수신 타임아웃");
                    }

                    _xmlInterface.Stop();
                });

                Console.WriteLine("수신된 측정 데이터 취득 시작...");

                // 여기서부터는 "설정 변경 -> 다음 프레임/평가 도착 대기 -> GetEvaluationResult" 가 안정적임.

                // 5. 이미지 저장 (원하는 타이밍에 새 프레임 한 번 기다려주면 더 안전)
                WaitFrameArrived("[5/7] 이미지 저장 전 최신 프레임 대기", 3000);

                Measure("TIF 파일 저장", () =>
                {
                    bool ok = _xmlInterface.SaveImageTif(Path.Combine(_outputPath, "SavedImage.tif"));
                    if (!ok) throw new Exception("TIF 파일 저장 실패");
                });
                Console.WriteLine("TIF 파일 저장 완료");

                // Beam size (최신 값 읽기)
                Measure("BEAMSIZE 조회", () =>
                {
                    var evds = _xmlInterface.GetEvaluationResult("FRAME_BEAMWIDTH_LONG");
                    _beamSizeLong = evds.val;

                    evds = _xmlInterface.GetEvaluationResult("FRAME_BEAMWIDTH_SHORT");
                    _beamSizeShort = evds.val;
                });
                Console.WriteLine($"Beam Size Long: {_beamSizeLong}");
                Console.WriteLine($"Beam Size Short: {_beamSizeShort}");

                // 6. Steepness - 크로스 섹션 위치 설정
                // 20 × 20 mm 영역에서 5개 지점 측정
                Console.WriteLine("Steepness 측정을 위한 세팅");

                Measure("CrossSection 세팅 적용", () =>
                {
                    _xmlInterface.FrameCrossSectionActive = true;
                    _xmlInterface.FrameCrossSectionAuto = false;
                });

                // 위치 1
                Measure("Steepness 위치 1 설정", () =>
                {
                    _xmlInterface.FrameCrossSectionRow = 3580;
                    _xmlInterface.FrameCrossSectionCol = 3647;
                });
                WaitFrameArrived("Steepness 위치 1 결과 대기", 3000);
                ReadVertical1090(out _vertical1090LengthAscent_1, out _vertical1090LengthDescent_1);
                Console.WriteLine($"Vertical 10-90 Length Ascent 1: {_vertical1090LengthAscent_1}");
                Console.WriteLine($"Vertical 10-90 Length Descent 1: {_vertical1090LengthDescent_1}");

                // 위치 2
                Measure("Steepness 위치 2 설정", () =>
                {
                    _xmlInterface.FrameCrossSectionRow = 5533;
                    _xmlInterface.FrameCrossSectionCol = 5600;
                });
                WaitFrameArrived("Steepness 위치 2 결과 대기", 3000);
                ReadVertical1090(out _vertical1090LengthAscent_2, out _vertical1090LengthDescent_2);
                Console.WriteLine($"Vertical 10-90 Length Ascent 2: {_vertical1090LengthAscent_2}");
                Console.WriteLine($"Vertical 10-90 Length Descent 2: {_vertical1090LengthDescent_2}");

                // 위치 3
                Measure("Steepness 위치 3 설정", () =>
                {
                    _xmlInterface.FrameCrossSectionRow = 5533;
                    _xmlInterface.FrameCrossSectionCol = 1694;
                });
                WaitFrameArrived("Steepness 위치 3 결과 대기", 3000);
                ReadVertical1090(out _vertical1090LengthAscent_3, out _vertical1090LengthDescent_3);
                Console.WriteLine($"Vertical 10-90 Length Ascent 3: {_vertical1090LengthAscent_3}");
                Console.WriteLine($"Vertical 10-90 Length Descent 3: {_vertical1090LengthDescent_3}");

                // 위치 4
                Measure("Steepness 위치 4 설정", () =>
                {
                    _xmlInterface.FrameCrossSectionRow = 1627;
                    _xmlInterface.FrameCrossSectionCol = 1694;
                });
                WaitFrameArrived("Steepness 위치 4 결과 대기", 3000);
                ReadVertical1090(out _vertical1090LengthAscent_4, out _vertical1090LengthDescent_4);
                Console.WriteLine($"Vertical 10-90 Length Ascent 4: {_vertical1090LengthAscent_4}");
                Console.WriteLine($"Vertical 10-90 Length Descent 4: {_vertical1090LengthDescent_4}");

                // 위치 5
                Measure("Steepness 위치 5 설정", () =>
                {
                    _xmlInterface.FrameCrossSectionRow = 1627;
                    _xmlInterface.FrameCrossSectionCol = 5600;
                });
                WaitFrameArrived("Steepness 위치 5 결과 대기", 3000);
                ReadVertical1090(out _vertical1090LengthAscent_5, out _vertical1090LengthDescent_5);
                Console.WriteLine($"Vertical 10-90 Length Ascent 5: {_vertical1090LengthAscent_5}");
                Console.WriteLine($"Vertical 10-90 Length Descent 5: {_vertical1090LengthDescent_5}");

                // 7) Uniformity ROI
                Measure("Uniformity ROI 설정", () =>
                {
                    _xmlInterface.BlurEnable = true;
                    _xmlInterface.BlurKernelValue = 3;

                    _xmlInterface.FrameROILeft = 1581;
                    _xmlInterface.FrameROITop = 1514;
                    _xmlInterface.FrameROIWidth = 4130;
                    _xmlInterface.FrameROIHeight = 4130;
                    _xmlInterface.FrameROIActive = true;
                });
                WaitFrameArrived("Uniformity 결과 대기", 3000);

                Measure("Uniformity 결과 읽기", () =>
                {
                    var evds = _xmlInterface.GetEvaluationResult("ROI2D_UNIFORMITY");
                    _uniformity = evds.val;

                    evds = _xmlInterface.GetEvaluationResult("ROI2D_MEAN");
                    _mean = evds.val;

                    evds = _xmlInterface.GetEvaluationResult("ROI2D_STDDEV");
                    _stdDev = evds.val;
                });

                Console.WriteLine($"Uniformity: {_uniformity}");
                Console.WriteLine($"Mean: {_mean}");
                Console.WriteLine($"Std Dev: {_stdDev}");

                _xmlInterface.Disconnect();

                Thread.Sleep(500);
            }
        }

        private static void LoadConfigIfProvided(string confPath)
        {
            if (string.IsNullOrWhiteSpace(confPath))
            {
                Console.WriteLine("Config 미지정: --conf <path> 로 설정파일을 지정할 수 있습니다.");
                return;
            }

            confPath = confPath.Trim();

            if (!File.Exists(confPath))
                throw new FileNotFoundException("Config 파일을 찾을 수 없습니다.", confPath);

            bool ok = _xmlInterface.LoadConfigFile(confPath);
            if (!ok)
                throw new Exception("Config 로드 실패 (LoadConfigFile returned false).");

            Console.WriteLine($"Config 로드 완료: {_xmlInterface.LoadedConfPath}");
        }

        private static string GetArgValue(string[] args, string longName, string shortName)
        {
            if (args == null || args.Length == 0)
                return null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, longName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, shortName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length)
                        return args[i + 1];

                    return null;
                }
            }

            return null;
        }

        private static void Measure(string title, Action action)
        {
            try
            {
                action();
                Console.WriteLine($"{title}");
            }
            catch
            {
                Console.WriteLine($"{title}");
                throw;
            }
        }

        private static void WaitFrameArrived(string title, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            bool ok = _frameArrived.WaitOne(timeoutMs);
            sw.Stop();

            if (!ok)
            {
                Console.WriteLine($"{title} : {sw.ElapsedMilliseconds} ms (TIMEOUT)");
                throw new TimeoutException($"{title} 타임아웃");
            }

            Console.WriteLine($"{title} : {sw.ElapsedMilliseconds} ms");
        }

        private static void ReadVertical1090(out string ascent, out string descent)
        {
            var evds = _xmlInterface.GetEvaluationResult("VERTICAL_1090_LENGTH_ASCENT");
            ascent = evds.val;

            evds = _xmlInterface.GetEvaluationResult("VERTICAL_1090_LENGTH_DESCENT");
            descent = evds.val;
        }

        // ---- 기존 이벤트 핸들러 (기기 정보 등 취득) ----

        private static void DisplayDeviceInfo()
        {
            Console.WriteLine($"  - 버전: {_xmlInterface.Version}");
            Console.WriteLine($"  - 카메라 ID: {_xmlInterface.CamID}");
            Console.WriteLine($"  - 해상도: {_xmlInterface.CamWidth} x {_xmlInterface.CamHeight}");
            Console.WriteLine($"  - Pixel Size: {_xmlInterface.CamPixelSizeWidth} x {_xmlInterface.CamPixelSizeHeight} um");
            Console.WriteLine($"  - Depth: {_xmlInterface.CamDepth} bit");

            if (_xmlInterface.CamAvailableGain)
                Console.WriteLine($"  - Gain: {_xmlInterface.CamGain} (범위: {_xmlInterface.CamGainMin} ~ {_xmlInterface.CamGainMax})");

            Console.WriteLine($"  - Exposure: {_xmlInterface.CamExposureTime} us (범위: {_xmlInterface.CamExposureTimeMin} ~ {_xmlInterface.CamExposureTimeMax})");
        }

        private static void OnErrorOccurred(object sender, EventArgs e)
        {
            string errMsg = _xmlInterface.ErrorDetail;
            Console.WriteLine($"에러 발생: {errMsg}");
        }

        private static void OnDisconnected(object sender, EventArgs e)
        {
            Console.WriteLine("장비와 연결이 끊어졌습니다.");
        }

        static private void OnFrameEvaluations(object sender, EventArgs e)
        {
            var dicEvals = sender as Dictionary<string, EvaluationDataSet>;
            if (dicEvals == null) return;

            if (dicEvals.TryGetValue("FRAME_NUMBER", out var evdsFrame))
                int.TryParse(evdsFrame.val, out _currframeNumber);

            // (원하는 시점에 1 프레임의 날짜 / 시간만 기록)
            if (_currframeNumber == 1)
            {
                if (dicEvals.TryGetValue("FRAME_DATE", out var evdsDate))
                    _frameDate = evdsDate.val;

                if (dicEvals.TryGetValue("FRAME_TIME", out var evdsTime))
                    _frameTime = evdsTime.val;
            }

            if (_currframeNumber <= _frameCount)
            {
                _frameArrived.Set();
            }
        }
    }
}