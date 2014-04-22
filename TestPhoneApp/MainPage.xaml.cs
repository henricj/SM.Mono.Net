using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using Microsoft.Phone.Controls;
using Microsoft.Phone.Info;
using Microsoft.Phone.Shell;
using TestPhoneApp.Resources;

namespace TestPhoneApp
{
    public static class ByteConversion
    {
        const double ToMiB = 1.0 / (1024 * 1024);

        public static double BytesToMiB(this long value)
        {
            return value * ToMiB;
        }
    }

    public partial class MainPage : PhoneApplicationPage
    {
        long _totalBytesRead;
        readonly DispatcherTimer _statusPoll;

        readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

        // Constructor
        public MainPage()
        {
            InitializeComponent();

            _statusPoll = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };

            _statusPoll.Tick += (sender, args) =>
            {
                download.Text = string.Format("{0:F2} MiB downloaded", Interlocked.Read(ref _totalBytesRead).BytesToMiB());

                var gcMemory = GC.GetTotalMemory(true).BytesToMiB();

                status.Text = string.Format("GC {0:F2} MiB App {1:F2}/{2:F2}/{3:F2} MiB", gcMemory,
                    DeviceStatus.ApplicationCurrentMemoryUsage.BytesToMiB(),
                    DeviceStatus.ApplicationPeakMemoryUsage.BytesToMiB(),
                    DeviceStatus.ApplicationMemoryUsageLimit.BytesToMiB());
            };
            
            // Sample code to localize the ApplicationBar
            //BuildLocalizedApplicationBar();
        }

        async Task DoSomethingWithData(byte[] data, int offset, int length)
        {
            // Simulate consuming the data at ~1MBit/s
            await Task.Delay((int)(length * (8.0 / 1000) + 0.5)).ConfigureAwait(false);

            // Blocking here by waiting for the delay to complete does NOT help.
            //Task.Delay((int) (length * (8.0 / 1000) + 0.5)).Wait();
        }

        async Task DownloadAsync()
        {
            var buffer = new byte[4096];

            var webRequest = SM.Mono.Net.WebRequest.Create("http://dds.cr.usgs.gov/pub/data/nationalatlas/elev48i0100a.tif_nt00828.tar.gz");

            using (var response = await webRequest.GetResponseAsync(_cancellationTokenSource.Token).ConfigureAwait(false))
            {
                using (var responseStream = response.GetResponseStream())
                {
                    for (; ; )
                    {
                        var length = await responseStream.ReadAsync(buffer, 0, buffer.Length, _cancellationTokenSource.Token).ConfigureAwait(false);

                        if (length < 1)
                            return;

                        Interlocked.Add(ref _totalBytesRead, length);

                        await DoSomethingWithData(buffer, 0, length).ConfigureAwait(false);
                    }
                }
            }
        }

        async void Button_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                message.Text = "Reading";

                var sw = Stopwatch.StartNew();

                await DownloadAsync();

                sw.Stop();

                message.Text = "Done in " + sw.Elapsed;
            }
            catch (Exception ex)
            {
                message.Text = "Failed: " + ex.Message;
            }
        }

        void PhoneApplicationPage_Loaded(object sender, RoutedEventArgs e)
        {
            _statusPoll.Start();
        }

        void PhoneApplicationPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _statusPoll.Stop();
        }

        // Sample code for building a localized ApplicationBar
        //private void BuildLocalizedApplicationBar()
        //{
        //    // Set the page's ApplicationBar to a new instance of ApplicationBar.
        //    ApplicationBar = new ApplicationBar();

        //    // Create a new button and set the text value to the localized string from AppResources.
        //    ApplicationBarIconButton appBarButton = new ApplicationBarIconButton(new Uri("/Assets/AppBar/appbar.add.rest.png", UriKind.Relative));
        //    appBarButton.Text = AppResources.AppBarButtonText;
        //    ApplicationBar.Buttons.Add(appBarButton);

        //    // Create a new menu item with the localized string from AppResources.
        //    ApplicationBarMenuItem appBarMenuItem = new ApplicationBarMenuItem(AppResources.AppBarMenuItemText);
        //    ApplicationBar.MenuItems.Add(appBarMenuItem);
        //}
    }
}