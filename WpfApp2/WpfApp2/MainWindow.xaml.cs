using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Emgu.CV;
using Emgu.CV.Structure;
using Microsoft.Win32;
using Emgu.CV.CvEnum;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using static WpfApp2.MainWindow;

namespace WpfApp2
{
    public partial class MainWindow : Window
    {
        private VideoCapture _capture;
        private DispatcherTimer _timer;
        private bool _isPlaying = false; // 재생 상태 파악 (Play/Pause 구분)
        private MyVideoViewModel _videoViewModel;

        public MainWindow()
        {
            InitializeComponent();
            _videoViewModel = new MyVideoViewModel();
            this.DataContext = _videoViewModel;
        }
        
        // Video Object Imformation
        public class MyVideo
        {
            public string name { get; set; }
        }

        // Video List Object
        public class MyVideoViewModel
        {
            public ObservableCollection<MyVideo> MyVideoes { get; set; }

            public MyVideoViewModel() 
            {
                MyVideoes = new ObservableCollection<MyVideo>();
            }

            public void AddVideo(string fileName)
            {
                MyVideo videoItem = new MyVideo();
                videoItem.name = fileName;
                MyVideoes.Add(new MyVideo { name = fileName});
            }
        }

        // 영상 재생과 관련된 함수들
        // (1) 영상 선택 버튼
        private void btnSelectVideo_Click(object sender, RoutedEventArgs e)
        {

            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Filter = "비디오 파일|*.mp4;*.avi;*.mov;*.wmv;*.mkv|모든 파일|*.*"
            };

            if (openFileDialog.ShowDialog() == true)
            {
                // 기존 자원 해제
                _capture?.Dispose();

                try
                {
                    // Emgu CV로 영상 디코딩
                    _capture = new VideoCapture(openFileDialog.FileName, VideoCapture.API.Any);

                    // MediaElement로 오디오 재생
                    mediaElement.Source = new Uri(openFileDialog.FileName);
                    mediaElement.Volume = sliderVolume.Value; // 볼륨 설정


                        try
                        {
                            // 파일명 추출
                            string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);
                            Console.WriteLine(fileName);

                            // 파일명이 유효한지 확인
                            if (string.IsNullOrEmpty(fileName))
                            {
                                MessageBox.Show("파일을 선택하지 않았습니다.");
                                //txtFileName.Text = "Why so serious?";
                                return;
                            }

                            // ViewModel이 null인지 확인
                            if (_videoViewModel == null)
                            {
                                _videoViewModel = new MyVideoViewModel(); // 초기화
                            }

                            // 파일명 추가
                            _videoViewModel.AddVideo(fileName);
                            // txtFileName.Text = fileName;

                            // UI에 파일명 표시 (옵션)
                            // txtFileName.Text = fileName;
                        }
                        catch (Exception ex)
                        {
                            // 예외 처리 (로그 기록 또는 사용자 알림)
                            MessageBox.Show($"오류가 발생했습니다: {ex.Message}");
                            //txtFileName.Text = "Error is coming";
                        }
                    

                    // 파일명 표시
                    //string fileName = System.IO.Path.GetFileName(openFileDialog.FileName);
                    //_videoViewModel.AddVideo(fileName);
                    // txtFileName.Text = System.IO.Path.GetFileName(openFileDialog.FileName);

                    // 타이머 (약 30fps)
                    if (_timer == null)
                    {
                        _timer = new DispatcherTimer();
                        _timer.Interval = TimeSpan.FromMilliseconds(33);
                        _timer.Tick += Timer_Tick;
                    }

                    // 비디오 작동바 가시화
                    show_VideoBar.Visibility = Visibility.Visible;

                    // 재생 시작(수정 -> 첫 장면에서 일시정지)
                    mediaElement.Position = TimeSpan.Zero; // 첫 프레임으로 이동
                    mediaElement.Pause();
                    _isPlaying = false;
                    btnPlayPause.Content = "▶"; // 재생 아이콘
                    _timer.Start();

                    // 첫 번째 프레임을 imgDisplay에 표시
                    using (Mat frame = new Mat())
                    {
                        _capture.Read(frame);
                        if (!frame.IsEmpty)
                        {
                            imgDisplay.Source = ToBitmapSource(frame);
                        }
                    }

                }
                catch (Exception ex)
                {
                    MessageBox.Show($"비디오 파일을 열 수 없습니다: {ex.Message}");
                }
            }
        }

        // (2) 미디어가 로드된 후(오디오/비디오 길이 알 수 있음)
        private void mediaElement_MediaOpened(object sender, RoutedEventArgs e)
        {
            if (mediaElement.NaturalDuration.HasTimeSpan)
            {
                double totalSec = mediaElement.NaturalDuration.TimeSpan.TotalSeconds;
                sliderSeekBar.Maximum = totalSec;
                txtTotalTime.Text = FormatTime(totalSec);
            }
        }

        // (3) 미디어가 끝까지 재생된 경우
        private void mediaElement_MediaEnded(object sender, RoutedEventArgs e)
        {
            // 자동 정지 + 위치 0으로 초기화
            StopPlayback();
        }

        // (4) 재생/일시정지 버튼
        private void btnPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (mediaElement.Source == null) return; // 파일이 없는 경우 무시

            if (_isPlaying)
            {
                // 현재 재생 중이면 -> 일시정지
                mediaElement.Pause();
                _isPlaying = false;
                btnPlayPause.Content = "▶";
            }
            else
            {
                // 일시정지 상태면 -> 재생
                mediaElement.Play();
                _isPlaying = true;
                btnPlayPause.Content = "❚❚";
            }
        }

        // (5) 정지 버튼
        private void btnStop_Click(object sender, RoutedEventArgs e)
        {
            if (mediaElement.Source == null) return;
            StopPlayback();
        }

        // 정지 로직(미디어/캡처 위치를 0으로, 타이머 진행도 멈춤)
        private void StopPlayback()
        {
            mediaElement.Stop();
            mediaElement.Position = TimeSpan.Zero;
            sliderSeekBar.Value = 0;
            txtCurrentTime.Text = "00:00:00";

            _capture?.Set(CapProp.PosMsec, 0);
            imgDisplay.Source = null;

            _isPlaying = false;
            btnPlayPause.Content = "▶";

            // 필요하다면 _timer는 계속 돌리거나, 멈출 수 있음
            // 여기서는 일단 계속 돌려서 업데이트 하도록 둠
            // _timer.Stop();
        }

        // (6) 타이머: 오디오 재생 위치에 맞춰 영상도 디코딩
        private void Timer_Tick(object sender, EventArgs e)
        {
            if (_capture == null || !_isPlaying) return;

            double currentSec = mediaElement.Position.TotalSeconds;
            sliderSeekBar.Value = currentSec;
            txtCurrentTime.Text = FormatTime(currentSec);

            // 영상도 동일 시각으로
            _capture.Set(CapProp.PosMsec, currentSec * 1000.0);

            using (Mat frame = new Mat())
            {
                _capture.Read(frame);
                if (!frame.IsEmpty)
                {
                    imgDisplay.Source = ToBitmapSource(frame);
                }
            }
        }

        // (7) 슬라이더를 클릭했을 때, 해당 위치로 즉시 이동
        private void sliderSeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            var slider = (System.Windows.Controls.Slider)sender;
            var clickPoint = e.GetPosition(slider);

            double ratio = clickPoint.X / slider.ActualWidth;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;

            double newValue = slider.Minimum + (slider.Maximum - slider.Minimum) * ratio;
            slider.Value = newValue;

            // 기본 드래그 동작 방지
            e.Handled = true;
        }

        // (8) 슬라이더 값이 바뀌면 -> 오디오/영상 위치도 이동
        private void sliderSeekBar_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (mediaElement.Source == null) return;
            if (!_isPlaying) return; // 재생 중일 때만 동기화

            mediaElement.Position = TimeSpan.FromSeconds(sliderSeekBar.Value);
            _capture?.Set(CapProp.PosMsec, sliderSeekBar.Value * 1000.0);
        }

        // (9) 볼륨 조절 (0~1)
        private void sliderVolume_ValueChanged(object sender,
            System.Windows.RoutedPropertyChangedEventArgs<double> e)
        {
            if (mediaElement != null)
            {
                mediaElement.Volume = sliderVolume.Value;
            }
        }

        // 시간(초)을 "hh:mm:ss" 형태로 변환
        private string FormatTime(double totalSeconds)
        {
            TimeSpan ts = TimeSpan.FromSeconds(totalSeconds);
            return ts.ToString(@"hh\:mm\:ss");
        }

        // Mat -> BitmapSource 변환
        private BitmapSource ToBitmapSource(Mat mat)
        {
            using (var bitmap = mat.ToBitmap())
            {
                IntPtr hBitmap = bitmap.GetHbitmap();
                BitmapSource bitmapSource = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                DeleteObject(hBitmap);
                return bitmapSource;
            }
        }

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr hObject);

        // 창 종료 시 자원 정리
        protected override void OnClosed(EventArgs e)
        {
            _timer?.Stop();
            _capture?.Dispose();
            mediaElement?.Stop();
            mediaElement.Source = null;
            base.OnClosed(e);
        }
    }
}
