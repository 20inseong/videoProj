using System;
using System.Windows;

namespace WpfApp2
{
    public partial class TrimVideoWindow : Window
    {
        public TrimVideoWindow()
        {
            InitializeComponent();
        }

        private void btnTrim_Click(object sender, RoutedEventArgs e)
        {
            if (!TimeSpan.TryParse(txtStartTime.Text, out TimeSpan startTime) ||
                !TimeSpan.TryParse(txtEndTime.Text, out TimeSpan endTime))
            {
                MessageBox.Show("올바른 시간을 입력하세요.");
                return;
            }

            if (endTime <= startTime)
            {
                MessageBox.Show("종료 시간은 시작 시간보다 커야 합니다.");
                return;
            }

            // MainWindow의 Trim 기능을 호출
            if (Owner is MainWindow mainWindow)
            {
                mainWindow.TrimVideoFromUI(startTime, endTime);
            }

            this.Close(); // 창 닫기
        }
    }
}
