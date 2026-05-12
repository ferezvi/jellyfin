using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Nikse.SubtitleEdit.Core.Common;
using Nikse.SubtitleEdit.Core.SubtitleFormats;

namespace MediaBrowser.MediaEncoding.Subtitles
{
    /// <summary>
    /// Extension methods for libse types.
    /// </summary>
    public static class LibseExtensions
    {
        /// <summary>
        /// Converts a subtitle to Jellyfin's JSON format.
        /// </summary>
        /// <param name="subtitle">The subtitle to convert.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A JSON string representing the subtitle in Jellyfin's format.</returns>
        public static string ToJson(this Subtitle subtitle, CancellationToken cancellationToken)
        {
            using var ms = new MemoryStream();
            using var jsonWriter = new Utf8JsonWriter(ms);
            jsonWriter.WriteStartObject();
            jsonWriter.WriteStartArray("TrackEvents");
            foreach (var paragraph in subtitle.Paragraphs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (line, position) = ExtractPosition(paragraph, subtitle);
                var text = CleanText(paragraph.Text);
                jsonWriter.WriteStartObject();
                jsonWriter.WriteString("Id", paragraph.Number.ToString(CultureInfo.InvariantCulture));
                jsonWriter.WriteString("Text", text);
                jsonWriter.WriteNumber("StartPositionTicks", paragraph.StartTime.TimeSpan.Ticks);
                jsonWriter.WriteNumber("EndPositionTicks", paragraph.EndTime.TimeSpan.Ticks);
                jsonWriter.WriteNumber("Line", line);
                jsonWriter.WriteNumber("Position", position);
                jsonWriter.WriteEndObject();
            }

            jsonWriter.WriteEndArray();
            jsonWriter.WriteEndObject();
            jsonWriter.Flush();
            return Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>
        /// Converts a subtitle to the specified format.
        /// </summary>
        /// <param name="subtitle">The subtitle to convert.</param>
        /// <param name="format">The output format.</param>
        /// <param name="cancellationToken">A token to monitor for cancellation requests.</param>
        /// <returns>A string representing the subtitle in the specified format.</returns>
        public static string ToText(this Subtitle subtitle, string format, CancellationToken cancellationToken)
        {
            // Reject null or empty format early with a clear exception
            ArgumentException.ThrowIfNullOrEmpty(format);
            // This is not a standard subtitle format, so we handle it separately before the switch.
            // It allows us to preserve metadata like position and line, which would be lost if we converted to a standard format and then to JSON.
            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
            {
                return subtitle.ToJson(cancellationToken);
            }

            SubtitleFormat formatter = format.ToUpperInvariant() switch
            {
                "ASS" => new AdvancedSubStationAlpha(),
                "SRT" => new SubRip(),
                "SSA" => new SubStationAlpha(),
                "VTT" or "WEBVTT" => new WebVTT(),
                "TTML" => new TimedText10(),
                _ => throw new ArgumentException($"Unsupported subtitle format: '{format}'.", nameof(format))
            };

            return formatter.ToText(subtitle, "untitled");
        }

        /// <summary>
        /// Extracts line and position from a subtitle paragraph.
        /// For WebVTT subtitles, reads the exact percentage values from the Style property,
        /// which libse populates when loading VTT files.
        /// For ASS subtitles, extracts \pos(x,y) from the text and converts to percentages
        /// using the subtitle resolution from the header.
        /// Falls back to parsing an {\anN} ASS alignment tag from the text for SRT and other formats.
        /// Returns (90, 50) — bottom center — when no position information is found.
        /// </summary>
        private static (int Line, int Position) ExtractPosition(Paragraph paragraph, Subtitle subtitle)
        {
            // WebVTT: extract positioning from the Style property (format: "position:50% line:90%")
            if (!string.IsNullOrEmpty(paragraph.Style))
            {
                var posMatch = Regex.Match(paragraph.Style, @"position:(?<position>\d+)%");
                var lineMatch = Regex.Match(paragraph.Style, @"line:(?<line>\d+)%");

                if (posMatch.Success && lineMatch.Success)
                {
                    int line = int.Parse(lineMatch.Groups["line"].Value, CultureInfo.InvariantCulture);
                    int position = int.Parse(posMatch.Groups["position"].Value, CultureInfo.InvariantCulture);
                    return (line, position);
                }
            }

            // ASS: extract \pos(x,y) from text
            if (paragraph.Text.Contains(@"{\pos", StringComparison.Ordinal))
            {
                var (resX, resY) = GetPlayRes(subtitle);
                var assPos = GetAssPosition(paragraph, resX, resY);
                if (assPos.HasValue)
                {
                    return assPos.Value;
                }
            }

            // Fallback: parse {\anN} tag from text (SRT, older formats)
            if (!string.IsNullOrEmpty(paragraph.Text))
            {
                var match = Regex.Match(paragraph.Text, @"^\{\\an([1-9])\}");
                if (match.Success)
                {
                    return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) switch
                    {
                        7 => (10, 10),
                        8 => (10, 50),
                        9 => (10, 90),
                        4 => (50, 10),
                        5 => (50, 50),
                        6 => (50, 90),
                        1 => (90, 10),
                        2 => (90, 50),
                        3 => (90, 90),
                        _ => (90, 50)
                    };
                }
            }

            // Default: bottom center
            return (90, 50);
        }

        /// <summary>
        /// Removes the {\anN} alignment tag that libse artificially prepends to subtitle text
        /// when converting from formats that lack a native position field (e.g. SRT).
        /// The positioning data has already been extracted by <see cref="ExtractPosition"/>
        /// before this method is called, so the tag is safe to remove for display.
        /// </summary>
        private static string CleanText(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return Regex.Replace(text, @"^\{\\an[1-9]\}", string.Empty);
        }

        /// <summary>
        /// Reads PlayResX and PlayResY from the subtitle header.
        /// Falls back to 1920x1080 when the header is absent or unparseable.
        /// </summary>
        private static (int Width, int Height) GetPlayRes(Subtitle subtitle)
        {
            var widthMatch = Regex.Match(subtitle.Header ?? string.Empty, @"PlayResX:\s*(\d+)");
            var heightMatch = Regex.Match(subtitle.Header ?? string.Empty, @"PlayResY:\s*(\d+)");
            int width = widthMatch.Success ? int.Parse(widthMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 1920;
            int height = heightMatch.Success ? int.Parse(heightMatch.Groups[1].Value, CultureInfo.InvariantCulture) : 1080;
            return (width, height);
        }

        /// <summary>
        /// Extracts \pos(x,y) from an ASS paragraph's Text and converts it to
        /// percentage-based line and position values using the subtitle resolution.
        /// Returns null when no \pos tag is found.
        /// </summary>
        private static (int Line, int Position)? GetAssPosition(Paragraph paragraph, int resX, int resY)
        {
            var match = Regex.Match(paragraph.Text, @"\{\\pos\((?<x>\d+(?:\.\d+)?),(?<y>\d+(?:\.\d+)?)\)");
            if (!match.Success)
            {
                return null;
            }

            double x = double.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
            double y = double.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            int position = (int)Math.Round(x / resX * 100);
            int line = (int)Math.Round(y / resY * 100);
            return (line, position);
        }
    }
}
