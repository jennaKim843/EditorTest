using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace InnoPVManagementSystem.Modules.CompareMerge.Services
{
    /// <summary>
    /// CSV 형태의 2차원 문자열 리스트(List&lt;List&lt;string&gt;&gt;)를
    /// 가독성 좋은 "표 텍스트"로 변환하는 유틸리티.
    ///
    /// 핵심:
    /// - 유니코드 범위로 폭을 추정하지 않고, WPF FormattedText로 "실제 렌더링 폭(픽셀)"을 측정해 열 너비를 계산한다.
    /// - 폰트 폴백(글리프 미지원으로 다른 폰트로 그려지는 경우)까지 반영되므로, Ⅱ/Ⅲ/Ⅳ 같은 문자도 정렬이 깨지지 않는다.
    /// - 좌/우를 동일 폭으로 맞추는 Pair 함수 포함 (Diff 노이즈 최소화).
    /// </summary>
    public static class CsvTableFormatter
    {
        // ===== 기본 렌더링 설정 (프로젝트에서 Diff 뷰가 쓰는 폰트/크기에 맞추는 게 가장 안정적) =====
        // ※ 폰트가 WPF에 로드되어 있어야 함(너는 ReadOnlyDiffView에서 LoadEmbeddedFontFamily로 로드 중)
        private static readonly FontFamily DefaultFontFamily = new FontFamily("NanumGothicCoding");
        private static readonly Typeface DefaultTypeface =
            new Typeface(DefaultFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        private const double DefaultFontSize = 12.0;

        // ===== 측정 캐시 (대용량 파일에서 성능) =====
        private sealed class MeasureContext
        {
            public readonly Typeface Typeface;
            public readonly double FontSize;
            public readonly double PixelsPerDip;
            public readonly CultureInfo Culture;

            // 문자열 폭 캐시: 같은 셀 값이 자주 반복되는 CSV에서 효과 큼
            private readonly Dictionary<string, double> _widthCache = new(StringComparer.Ordinal);
            private readonly Dictionary<int, string> _spacesCache = new(); // n -> "     "

            public readonly double SpaceWidth;

            public MeasureContext(Typeface typeface, double fontSize, double pixelsPerDip, CultureInfo culture)
            {
                Typeface = typeface;
                FontSize = fontSize;
                PixelsPerDip = pixelsPerDip;
                Culture = culture;
                SpaceWidth = MeasureRaw(" "); // 공백 폭도 실제 렌더링 기준
                if (SpaceWidth <= 0.0001) SpaceWidth = 4.0; // 안전장치
            }

            public double Measure(string? s)
            {
                if (string.IsNullOrEmpty(s)) return 0;

                // 탭 같은 게 섞이면 폭이 예측 불가해져서, 표 출력용으로 공백으로 치환
                s = s.Replace("\t", "    ");

                if (_widthCache.TryGetValue(s, out var w))
                    return w;

                w = MeasureRaw(s);
                _widthCache[s] = w;
                return w;
            }

            private double MeasureRaw(string s)
            {
#if NET5_0_OR_GREATER
                var ft = new FormattedText(
                    s,
                    Culture,
                    FlowDirection.LeftToRight,
                    Typeface,
                    FontSize,
                    Brushes.Black,
                    PixelsPerDip);
                return ft.WidthIncludingTrailingWhitespace;
#else
                var ft = new FormattedText(
                    s,
                    Culture,
                    FlowDirection.LeftToRight,
                    Typeface,
                    FontSize,
                    Brushes.Black);
                return ft.Width;
#endif
            }

            public string Spaces(int n)
            {
                if (n <= 0) return string.Empty;
                if (_spacesCache.TryGetValue(n, out var s))
                    return s;
                s = new string(' ', n);
                _spacesCache[n] = s;
                return s;
            }
        }

        /// <summary>
        /// 단일 CSV 데이터 → 표 형태 문자열로 변환.
        /// 첫 행을 헤더로 처리하여 구분선을 추가한다.
        /// </summary>
        public static string ToTableText(List<List<string>> data)
            => ToTableText(data, DefaultTypeface, DefaultFontSize);

        /// <summary>
        /// 단일 CSV 데이터 → 표 형태 문자열로 변환(폰트/크기 지정 가능).
        /// DiffView에서 실제 사용하는 폰트/크기와 동일하게 넣으면 가장 정확.
        /// </summary>
        public static string ToTableText(List<List<string>> data, Typeface typeface, double fontSize)
        {
            if (data == null || data.Count == 0)
                return string.Empty;

            var ctx = CreateMeasureContext(typeface, fontSize);

            var widths = CalcColPixelWidths(data, ctx);
            var sb = new StringBuilder();

            for (int i = 0; i < data.Count; i++)
            {
                sb.AppendLine(BuildRow(data[i], widths, ctx));
                if (i == 0)
                    sb.AppendLine(BuildSeparator(widths, ctx));
            }

            return sb.ToString();
        }

        /// <summary>
        /// 좌/우 CSV 데이터를 동일한 열 폭으로 포맷팅하여
        /// Diff 결과가 안정적으로 비교되도록 맞춰 반환.
        /// </summary>
        public static (string left, string right) ToTableTextPair(List<List<string>> left, List<List<string>> right)
            => ToTableTextPair(left, right, DefaultTypeface, DefaultFontSize);

        /// <summary>
        /// 좌/우 CSV 데이터를 동일한 열 폭으로 포맷팅(Pixel width 기준).
        /// </summary>
        public static (string left, string right) ToTableTextPair(
            List<List<string>> left,
            List<List<string>> right,
            Typeface typeface,
            double fontSize)
        {
            var ctx = CreateMeasureContext(typeface, fontSize);

            var widths = MergeWidths(
                CalcColPixelWidths(left, ctx),
                CalcColPixelWidths(right, ctx));

            return (BuildTable(left, widths, ctx), BuildTable(right, widths, ctx));
        }

        // ====== 폭 계산 (픽셀) ======

        private static double[] CalcColPixelWidths(List<List<string>> rows, MeasureContext ctx)
        {
            if (rows == null || rows.Count == 0)
                return Array.Empty<double>();

            int colCount = rows.Max(r => r?.Count ?? 0);
            if (colCount <= 0) return Array.Empty<double>();

            var widths = new double[colCount];

            foreach (var row in rows)
            {
                if (row == null) continue;

                for (int c = 0; c < row.Count; c++)
                {
                    string cell = row[c] ?? string.Empty;
                    double w = ctx.Measure(cell);
                    if (w > widths[c])
                        widths[c] = w;
                }
            }

            return widths;
        }

        private static double[] MergeWidths(double[] a, double[] b)
        {
            int n = Math.Max(a.Length, b.Length);
            var w = new double[n];
            for (int i = 0; i < n; i++)
            {
                double la = i < a.Length ? a[i] : 0;
                double lb = i < b.Length ? b[i] : 0;
                w[i] = Math.Max(la, lb);
            }
            return w;
        }

        // ====== 패딩 (픽셀 목표 폭까지 공백 추가) ======

        private static string PadCellToPixelWidth(string? text, double targetWidth, MeasureContext ctx)
        {
            string s = text ?? string.Empty;

            // 표 출력에서 탭은 정렬을 깨기 쉬워서 공백으로 치환
            s = s.Replace("\t", "    ");

            double w = ctx.Measure(s);
            if (w >= targetWidth) return s;

            double remain = targetWidth - w;

            // 1차 근사: 공백 폭 기준으로 필요한 개수 산출
            int padCount = (int)Math.Ceiling(remain / ctx.SpaceWidth);
            if (padCount < 0) padCount = 0;

            // 보정: 폴백/커닝/렌더링 차이로 1~2칸 오차가 날 수 있어 짧게 보정 루프
            // (대용량에서도 부담 없게 최대 3회)
            string candidate = s + ctx.Spaces(padCount);
            for (int i = 0; i < 3; i++)
            {
                double cw = ctx.Measure(candidate);
                if (cw >= targetWidth)
                {
                    // 혹시 과하게 붙였으면 1칸씩 줄여보되, 목표 폭 아래로 내려가면 중단
                    while (padCount > 0)
                    {
                        string trimmed = s + ctx.Spaces(padCount - 1);
                        if (ctx.Measure(trimmed) >= targetWidth)
                        {
                            padCount--;
                            candidate = trimmed;
                            continue;
                        }
                        break;
                    }
                    return candidate;
                }

                padCount++;
                candidate = s + ctx.Spaces(padCount);
            }

            return candidate;
        }

        // ====== 행/구분선 생성 ======

        private static string BuildRow(IReadOnlyList<string> row, double[] widths, MeasureContext ctx)
        {
            var sb = new StringBuilder();
            sb.Append("| ");

            for (int i = 0; i < widths.Length; i++)
            {
                string cell = row != null && i < row.Count ? row[i] ?? string.Empty : string.Empty;
                sb.Append(PadCellToPixelWidth(cell, widths[i], ctx));
                sb.Append(" | ");
            }

            return sb.ToString().TrimEnd();
        }

        private static string BuildSeparator(double[] widths, MeasureContext ctx)
        {
            var sb = new StringBuilder();
            sb.Append("|");

            foreach (var target in widths)
            {
                sb.Append(' ');

                // 1) '-'로 대충 채우기 (target 근처까지)
                string seg = BuildDashSegment(target, ctx);

                // 2) 최종적으로 target 픽셀폭에 "정확히" 맞추기 (space로 미세조정)
                seg = PadOrTrimToPixelWidth(seg, target, ctx);

                sb.Append(seg);
                sb.Append(" |");
            }

            return sb.ToString();
        }

        private static string BuildDashSegment(double targetPx, MeasureContext ctx)
        {
            double dashPx = Math.Max(1.0, ctx.Measure("-"));

            // 근사치로 시작
            int n = Math.Max(1, (int)Math.Round(targetPx / dashPx));
            string s = new string('-', n);

            // 부족하면 추가
            while (ctx.Measure(s) < targetPx)
                s += "-";

            return s;
        }

        private static string PadOrTrimToPixelWidth(string s, double targetPx, MeasureContext ctx)
        {
            // 1) 부족하면 space로 채움 (셀 패딩과 같은 원리)
            while (ctx.Measure(s) < targetPx)
                s += " ";

            // 2) 넘치면 뒤에서부터 줄임
            //    - space가 있으면 space부터 제거
            //    - 그 다음 '-' 제거
            //    - 제거 후 부족해지면 다시 space로 보정
            while (s.Length > 1 && ctx.Measure(s) > targetPx)
            {
                s = s[..^1];

                while (ctx.Measure(s) < targetPx)
                    s += " ";
            }

            return s;
        }

        private static string BuildTable(List<List<string>> data, double[] widths, MeasureContext ctx)
        {
            var sb = new StringBuilder();
            if (data == null || data.Count == 0) return "";

            sb.AppendLine(BuildRow(data[0], widths, ctx));
            sb.AppendLine(BuildSeparator(widths, ctx));

            for (int i = 1; i < data.Count; i++)
                sb.AppendLine(BuildRow(data[i], widths, ctx));

            return sb.ToString();
        }

        // ====== MeasureContext 생성 ======

        private static MeasureContext CreateMeasureContext(Typeface typeface, double fontSize)
        {
            // DPI는 실제 화면/윈도우에 따라 달라질 수 있지만,
            // Diff 텍스트 정렬은 "같은 프로세스/같은 렌더링 환경에서" 좌우를 맞추는 게 목적이라
            // 우선 Application이 있으면 거기 DPI를 따르고, 없으면 1.0으로 둔다.
            double ppd = 1.0;

            try
            {
                if (Application.Current?.MainWindow != null)
                {
                    var dpi = VisualTreeHelper.GetDpi(Application.Current.MainWindow);
                    ppd = dpi.PixelsPerDip;
                }
            }
            catch
            {
                ppd = 1.0;
            }

            return new MeasureContext(
                typeface ?? DefaultTypeface,
                fontSize <= 0 ? DefaultFontSize : fontSize,
                ppd,
                CultureInfo.CurrentUICulture);
        }


        public static string ToTableText(List<List<string>> data, FontFamily fontFamily, double fontSize)
        {
            var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return ToTableText(data, typeface, fontSize);
        }

        public static (string left, string right) ToTableTextPair(
            List<List<string>> left,
            List<List<string>> right,
            FontFamily fontFamily,
            double fontSize)
        {
            var typeface = new Typeface(fontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            return ToTableTextPair(left, right, typeface, fontSize);
        }

    }
}
