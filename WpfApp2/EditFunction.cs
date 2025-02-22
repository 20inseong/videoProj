using System;
using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.IO;

namespace WpfApp2
{
    public class EditFunction
    {
        private void ExecuteFFmpegCommand(string arguments, IProgress<string> progress, CancellationToken cancellationToken)
        {
            using (Process ffmpeg = new Process())
            {
                ffmpeg.StartInfo.FileName = @"C:\Program Files\ffmpeg\ffmpeg-7.0.2-full_build-shared\bin\ffmpeg.exe";
                ffmpeg.StartInfo.Arguments = arguments;
                ffmpeg.StartInfo.UseShellExecute = false;
                ffmpeg.StartInfo.RedirectStandardOutput = true;
                ffmpeg.StartInfo.RedirectStandardError = true;
                ffmpeg.StartInfo.CreateNoWindow = true;

                ffmpeg.Start();

                while (!ffmpeg.StandardError.EndOfStream)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string line = ffmpeg.StandardError.ReadLine();
                    progress.Report(line);
                }

                ffmpeg.WaitForExit();

                if (ffmpeg.ExitCode != 0)
                {
                    throw new Exception($"FFmpeg command failed with exit code: {ffmpeg.ExitCode}");
                }
            }
        }

        public void TrimVideo(string inputFile, string outputFile, TimeSpan startTime, TimeSpan endTime, IProgress<string> progress, CancellationToken cancellationToken)
        {
            try
            {
                if (!System.IO.File.Exists(inputFile))
                {
                    string message = $"입력 파일을 찾을 수 없습니다: {inputFile}";
                    Console.WriteLine(message);
                    MessageBox.Show(message, "오류");
                    return;
                }

                TimeSpan duration = endTime - startTime;
                string arguments = $"-ss {TimeSpanToSeconds(startTime)} -i \"{inputFile}\" -t {TimeSpanToSeconds(duration)} -c copy \"{outputFile}\"";

                ExecuteFFmpegCommand(arguments, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }


        private string TimeSpanToSeconds(TimeSpan ts)
        {
            return ts.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public void ConcatenateVideos(string firstVideo, string secondVideo, string outputFile, IProgress<string> progress, CancellationToken cancellationToken)
        {
            try
            {
                if (!System.IO.File.Exists(firstVideo) || !System.IO.File.Exists(secondVideo))
                {
                    string message = "입력 파일을 찾을 수 없습니다.";
                    Console.WriteLine(message);
                    MessageBox.Show(message, "오류");
                    return;
                }

                string arguments = $"-i \"{firstVideo}\" -i \"{secondVideo}\" -filter_complex \"[0:v:0][0:a:0][1:v:0][1:a:0]concat=n=2:v=1:a=1[outv][outa]\" -map \"[outv]\" -map \"[outa]\" \"{outputFile}\"";

                ExecuteFFmpegCommand(arguments, progress, cancellationToken);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                throw;
            }
        }
    }
}