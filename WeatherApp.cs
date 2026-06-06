using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;

// Force Application to always refer to WinForms
using Application = System.Windows.Forms.Application;

namespace WeatherIdleOverlay
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new WeatherOverlayForm());
        }
    }

    public static class IniFile
    {
        public static string Read(string path, string section, string key, string defaultValue = "")
        {
            if (!File.Exists(path))
                return defaultValue;

            string[] lines = File.ReadAllLines(path);
            string currentSection = "";

            foreach (string raw in lines)
            {
                string line = raw.Trim();

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    currentSection = line.Substring(1, line.Length - 2);
                    continue;
                }

                if (currentSection.Equals(section, StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Split('=');
                    if (parts.Length == 2 && parts[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                        return parts[1].Trim();
                }
            }

            return defaultValue;
        }
    }

    public class WeatherOverlayForm : Form
    {
        private static string ToCompass(double degrees)
        {
            if (double.IsNaN(degrees)) return "--";
            string[] dirs = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return dirs[(int)Math.Round(((degrees % 360) / 45)) % 8];
        }

        public double WindSpeedMph { get; set; }
        public string WindDirection { get; set; } = "";
        public double BarometricPressureInHg { get; set; }

        private bool _debugging = true;

        // UI controls
        private Label _lblCurrentTemp;
        private Label _lblHighLow;
        private Label _lblHumidity;
        private Label _lblSunriseSunset;

        private Label _lblLocation;
        private Label _lblDate;
        private Label _lblTime;
        private Label _lblLastUpdated;

        private ListView _lvSevenDay;
        private Chart _chartHourlyRain;

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly JavaScriptSerializer _json = new JavaScriptSerializer();

        private readonly Timer _weatherTimer = new Timer();
        private readonly Timer _clockTimer = new Timer();
        private DateTime _lastUpdated = DateTime.MinValue;

        // Your coordinates
        private static double Lat;
        private static double Lon;

        public static void EnableDoubleBuffer(Control c)
        {
            c.GetType().GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance)
                ?.SetValue(c, true, null);
        }

        public WeatherOverlayForm()
        {
            string iniPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WeatherApp.ini");

            Lat = double.Parse(IniFile.Read(iniPath, "Location", "Latitude", "45.688867"));
            Lon = double.Parse(IniFile.Read(iniPath, "Location", "Longitude", "-122.651783"));

            _httpClient.DefaultRequestHeaders.Add("User-Agent", "WeatherApp/1.0 (Q@example.com)");

            AutoScaleMode = AutoScaleMode.None;
            AutoScaleDimensions = new SizeF(96F, 96F);

            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            if (!_debugging) TopMost = true;
            BackColor = Color.FromArgb(15, 40, 90);
            ForeColor = Color.White;
            KeyPreview = true;
            ShowInTaskbar = false;

            this.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                    Application.Exit();
            };

            BuildUi();
            SetupClock();
            _weatherTimer.Interval = 5 * 60 * 1000; // 5 minutes
            _weatherTimer.Tick += async (s, e) => await RefreshWeatherSafeAsync();
            _weatherTimer.Start();

            Opacity = 1;
            Visible = true;
            BringToFront();
            if (!_debugging) TopMost = true;

            _ = RefreshWeatherSafeAsync();
       }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _clockTimer.Stop();
            _httpClient.Dispose();
            base.OnFormClosed(e);
        }

        private void SetupClock()
        {
            _clockTimer.Interval = 1000;
            _clockTimer.Tick += (s, e) =>
            {
                var now = DateTime.Now;
                _lblDate.Text = now.ToString("MMMM dd, yyyy");
                _lblTime.Text = now.ToString("hh:mm:ss tt");
                if (_lastUpdated != DateTime.MinValue)
                    _lblLastUpdated.Text = $"(last updated {_lastUpdated:hh:mm:ss tt})";
                else
                    _lblLastUpdated.Text = "(last updated --:--:--)";
            };
            _clockTimer.Start();
        }

        private void LvSevenDay_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void LvSevenDay_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            // Column index of "% Chance of Rain"
            int popColumnIndex = 4;

            // If it's not the PoP column, draw normally
            if (e.ColumnIndex != popColumnIndex)
            {
                e.DrawDefault = true;
                return;
            }

            // Parse the percentage (e.g., "65%")
            string text = e.SubItem.Text.Replace("%", "");
            if (!double.TryParse(text, out double pop))
                pop = 0;

            // Pick color
            Color fg;
            if (pop < 30)
                fg = Color.FromArgb(255, 150, 255, 200);   // light green
            else if (pop <= 60)
                fg = Color.FromArgb(255, 255, 200, 150);   // orange-ish
            else
                fg = Color.FromArgb(255, 255, 100, 100);   // red

            // Fill background
            //using (var b = new SolidBrush(fg))
            //    e.Graphics.FillRectangle(b, e.Bounds);

            // Draw centered text
            TextRenderer.DrawText(
                e.Graphics,
                e.SubItem.Text,
                _lvSevenDay.Font,
                e.Bounds,
                fg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            );
        }

        // ====== UI BUILD ======
        private void BuildUi()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                BackColor = BackColor,
                Padding = new Padding(20)
            };

            root.RowStyles.Clear();
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 340)); // top: current + location/clock
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 55));  // 7-day
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 45));  // chart

            // ===== Top row: two columns =====
            var topRow = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = BackColor
            };
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            topRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

            // Left: current conditions
            var panelCurrent = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor,
                Padding = new Padding(0)
            };

            panelCurrent.RowStyles.Clear();
            panelCurrent.RowStyles.Add(new RowStyle(SizeType.Absolute, 160)); // temp
            panelCurrent.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));  // high/low
            panelCurrent.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));  // humidity
            panelCurrent.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // sunrise/sunset

            _lblCurrentTemp = new Label
            {
                AutoSize = false,
                Font = new Font("Segoe UI", 90, FontStyle.Bold),
                TextAlign = ContentAlignment.TopLeft,
                Text = "--°",
                Dock = DockStyle.None,
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Margin = new Padding(-20, -20, 0, 0),   // moves it UP
                Padding = new Padding(0),
                Left = -10,
                Top = -15,
                Width = 500,
                Height = 160,
                Tag = "CurrentTemp"
            };
            this.Controls.Add(_lblCurrentTemp);

            _lblHighLow = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 24, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "L: --°  H: --°"
            };
            _lblHumidity = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 24, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Humidity: --%"
            };
            _lblSunriseSunset = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 24, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "Sunrise: --:--  Sunset: --:--"
            };

            panelCurrent.Controls.Add(_lblHighLow, 0, 1);
            panelCurrent.Controls.Add(_lblHumidity, 0, 2);
            panelCurrent.Controls.Add(_lblSunriseSunset, 0, 3);

            // Right: location + date/time + last updated
            var panelRight = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 4,
                BackColor = BackColor,
                Padding = new Padding(0)
            };

            panelRight.RowStyles.Clear();
            panelRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));  // location
            panelRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));  // date
            panelRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));  // time
            panelRight.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));  // last updated

            _lblLocation = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 40, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleRight,
                Text = "Loading location..."
            };
            _lblDate = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 32, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleRight,
                Text = ""
            };
            _lblTime = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 32, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleRight,
                Text = ""
            };
            _lblLastUpdated = new Label
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                Font = new Font("Segoe UI", 10, FontStyle.Italic),
                TextAlign = ContentAlignment.BottomRight,
                Text = "(last updated --:--:--)"
            };

            panelRight.Controls.Add(_lblLocation, 0, 0);
            panelRight.Controls.Add(_lblDate, 0, 1);
            panelRight.Controls.Add(_lblTime, 0, 2);
            panelRight.Controls.Add(_lblLastUpdated, 0, 3);

            topRow.Controls.Add(panelCurrent, 0, 0);
            topRow.Controls.Add(panelRight, 1, 0);

            // ===== Middle: 7-day forecast =====
            _lvSevenDay = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                Font = new Font("Segoe UI", 18),
                BackColor = Color.FromArgb(10, 40, 100),
                ForeColor = Color.White,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            _lvSevenDay.OwnerDraw = true;
            _lvSevenDay.DrawColumnHeader += LvSevenDay_DrawColumnHeader;
            _lvSevenDay.DrawSubItem += LvSevenDay_DrawSubItem;

            _lvSevenDay.Columns.Add("Day", 220);
            _lvSevenDay.Columns.Add("Summary", 600);
            _lvSevenDay.Columns.Add("Low", 120);     // moved before High
            _lvSevenDay.Columns.Add("High", 120);
            _lvSevenDay.Columns.Add("% Chance of Rain", 250, HorizontalAlignment.Center);

            // ===== Bottom: hourly rain chart =====
            _chartHourlyRain = new Chart
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(10, 40, 100),
                ForeColor = Color.White
            };

            var chartArea = new ChartArea("RainArea")
            {
                BackColor = Color.FromArgb(10, 40, 100),
                AxisX =
                {
                    MajorGrid = { Enabled = false },
                    LabelStyle = { ForeColor = Color.White, Font = new Font("Segoe UI", 12) },
                    LineColor = Color.White
                },
                AxisY =
                {
                    Minimum = 0,
                    Maximum = 100,
                    Interval = 20,
                    MajorGrid = { LineColor = Color.FromArgb(40, 80, 140) },
                    LabelStyle = { ForeColor = Color.White, Font = new Font("Segoe UI", 12) },
                    LineColor = Color.White,
                    Title = "Rain %",
                    TitleFont = new Font("Segoe UI", 12, FontStyle.Bold),
                    TitleForeColor = Color.White
                }
            };

            chartArea.AxisX.Interval = 1;
            chartArea.AxisX.LabelStyle.IsEndLabelVisible = true;
            chartArea.AxisX.LabelStyle.Angle = -45;   // optional but helps spacing

            _chartHourlyRain.ChartAreas.Add(chartArea);

            var series = new Series("Rain")
            {
                ChartType = SeriesChartType.Column,
                XValueType = ChartValueType.String,
                YValueType = ChartValueType.Double,
                IsValueShownAsLabel = true,
                LabelForeColor = Color.White,
                Font = new Font("Segoe UI", 10, FontStyle.Bold)
            };
            _chartHourlyRain.Series.Add(series);

            root.Controls.Add(topRow, 0, 0);
            root.Controls.Add(_lvSevenDay, 0, 1);
            root.Controls.Add(_chartHourlyRain, 0, 2);

            Controls.Add(root);

            EnableDoubleBuffer(this);    // Enable true double-buffering
            this.SetStyle(ControlStyles.AllPaintingInWmPaint |
                          ControlStyles.UserPaint |
                          ControlStyles.OptimizedDoubleBuffer, true);
            this.UpdateStyles();

            EnableDoubleBuffer(panelRight);   // the panel with time/date
            EnableDoubleBuffer(root);         // optional
            EnableDoubleBuffer(topRow);       // optional
        }

        // ====== WEATHER FETCHING ======
        private async Task RefreshWeatherSafeAsync()
        {
            try
            {
                var data = await FetchWeatherAsync();
                if (data != null)
                {
                    _lastUpdated = DateTime.Now;
                    UpdateUiWithWeather(data);
                }
            }
            catch
            {
                // swallow
            }
        }

        private async Task<WeatherData> FetchWeatherAsync()
        {
            var data = new WeatherData();

            // 1. Points metadata
            string pointUrl = $"https://api.weather.gov/points/{Lat},{Lon}";
            var pointJson = _json.Deserialize<Dictionary<string, object>>(
                await _httpClient.GetStringAsync(pointUrl)
            );
            var props = (Dictionary<string, object>)pointJson["properties"];

            string forecastUrl = props["forecast"].ToString();
            string hourlyUrl = props["forecastHourly"].ToString();
            string stationsUrl = props["observationStations"].ToString();

            // relativeLocation -> city/state
            if (props.TryGetValue("relativeLocation", out var relObj) && relObj is Dictionary<string, object> relDict)
            {
                if (relDict.TryGetValue("properties", out var relPropsObj) && relPropsObj is Dictionary<string, object> relProps)
                {
                    if (relProps.TryGetValue("city", out var cityObj))
                        data.City = cityObj?.ToString() ?? "";
                    if (relProps.TryGetValue("state", out var stateObj))
                        data.State = stateObj?.ToString() ?? "";
                }
            }

            // 2. Stations
            var stationsJson = _json.Deserialize<Dictionary<string, object>>(
                await _httpClient.GetStringAsync(stationsUrl)
            );
            var features = (ArrayList)stationsJson["features"];
            var first = (Dictionary<string, object>)features[0];
            var stProps = (Dictionary<string, object>)first["properties"];
            string stationId = stProps["stationIdentifier"].ToString();

            // 3. Current observations
            string obsUrl = $"https://api.weather.gov/stations/{stationId}/observations/latest";
            var obsJson = _json.Deserialize<Dictionary<string, object>>(
                await _httpClient.GetStringAsync(obsUrl)
            );
            var obsProps = (Dictionary<string, object>)obsJson["properties"];

            var tempDict = (Dictionary<string, object>)obsProps["temperature"];
            var tempVal = tempDict["value"];
            double tempC = tempVal == null ? 0 : Convert.ToDouble(tempVal);
            data.CurrentTempF = tempC * 9 / 5 + 32;

            var humDict = (Dictionary<string, object>)obsProps["relativeHumidity"];
            var humVal = humDict["value"];
            data.HumidityPercent = humVal == null ? 0 : Convert.ToInt32(humVal);
            // Wind speed (m/s) — sustained only
            var windSpeedDict = (Dictionary<string, object>)obsProps["windSpeed"];
            var windSpeedVal = windSpeedDict["value"];

            // If null → treat as calm
            double windMps = windSpeedVal == null ? 0 : Convert.ToDouble(windSpeedVal);
            // Convert to mph
            double windRaw = windSpeedVal == null ? 0 : Convert.ToDouble(windSpeedVal);
            // Treat as km/h
            double windKmh = windRaw;
            windMps = windKmh / 3.6;
            data.WindSpeedMph = windMps * 2.23694;

            // Wind direction (degrees)
            var windDirDict = (Dictionary<string, object>)obsProps["windDirection"];
            var windDirVal = windDirDict["value"];
            data.WindDirectionDegrees = windDirVal == null ? double.NaN : Convert.ToDouble(windDirVal);

            var pressureDict = (Dictionary<string, object>)obsProps["barometricPressure"];
            var pressureVal = pressureDict["value"];
            double pressurePa = pressureVal == null ? 0 : Convert.ToDouble(pressureVal);
            data.BarometricPressureInHg = pressurePa * 0.0002953;   // Pascals → inHg

            // 4. 7-day forecast
            var forecastJson = _json.Deserialize<Dictionary<string, object>>(
                await _httpClient.GetStringAsync(forecastUrl)
            );
            var fProps = (Dictionary<string, object>)forecastJson["properties"];
            var periods = (ArrayList)fProps["periods"];

            data.Daily.Clear();
            foreach (Dictionary<string, object> p in periods)
            {
                bool isDay = (bool)p["isDaytime"];
                if (!isDay) continue;

                var df = new DailyForecast
                {
                    Date = DateTime.Parse(p["startTime"].ToString()).Date,
                    HighF = Convert.ToDouble(p["temperature"]),
                    Summary = p["shortForecast"].ToString()
                };

                var popDict = (Dictionary<string, object>)p["probabilityOfPrecipitation"];
                var popVal = popDict["value"];
                df.ProbabilityOfPrecip = popVal == null ? 0 : Convert.ToDouble(popVal);


                foreach (Dictionary<string, object> p2 in periods)
                {
                    if ((bool)p2["isDaytime"]) continue;
                    if (DateTime.Parse(p2["startTime"].ToString()).Date == df.Date)
                    {
                        df.LowF = Convert.ToDouble(p2["temperature"]);
                        break;
                    }
                }

                data.Daily.Add(df);
                if (data.Daily.Count >= 7) break;
            }

            if (data.Daily.Count > 0)
            {
                data.TodayHighF = data.Daily[0].HighF;
                data.TodayLowF = data.Daily[0].LowF;
            }

            // 5. Hourly rain
            var hourlyJson = _json.Deserialize<Dictionary<string, object>>(
                await _httpClient.GetStringAsync(hourlyUrl)
            );
            var hProps = (Dictionary<string, object>)hourlyJson["properties"];
            var hPeriods = (ArrayList)hProps["periods"];

            data.HourlyRain.Clear();
            int count = 0;
            foreach (Dictionary<string, object> h in hPeriods)
            {
                if (count >= 24) break;

                var pop = (Dictionary<string, object>)h["probabilityOfPrecipitation"];
                var popVal = pop["value"];
                double val = popVal == null ? 0 : Convert.ToDouble(popVal);

                data.HourlyRain.Add(new HourlyRainChance
                {
                    Time = DateTime.Parse(h["startTime"].ToString()),
                    Probability = val / 100.0
                });

                count++;
            }

            // 6. Sunrise / Sunset
            try
            {
                string ssUrl = $"https://api.sunrise-sunset.org/json?lat={Lat}&lng={Lon}&formatted=0";
                var ssJson = _json.Deserialize<Dictionary<string, object>>(
                    await _httpClient.GetStringAsync(ssUrl)
                );
                var ssProps = (Dictionary<string, object>)ssJson["results"];

                data.Sunrise = DateTime.Parse(ssProps["sunrise"].ToString()).ToLocalTime();
                data.Sunset = DateTime.Parse(ssProps["sunset"].ToString()).ToLocalTime();

            }
            catch
            {
                data.Sunrise = DateTime.MinValue;
                data.Sunset = DateTime.MinValue;
            }

            return data;
        }
        private void MakeTransparent(Control ctrl, int x, int y)
        {
            Bitmap bMap = new Bitmap(this.BackgroundImage);
            Color[,] pixelArray = new Color[ctrl.Width, ctrl.Height];

            for (int i = 0; i < ctrl.Width; i++)
            {
                for (int j = 0; j < ctrl.Height; j++)
                {
                    pixelArray[i, j] = bMap.GetPixel(x + i, y + j);
                }
            }

            Bitmap bmp = new Bitmap(ctrl.Width, ctrl.Height);

            for (int i = 0; i < ctrl.Width; i++)
            {
                for (int j = 0; j < ctrl.Height; j++)
                {
                    bmp.SetPixel(i, j, pixelArray[i, j]);
                }
            }

            ctrl.BackgroundImage = bmp;
            ctrl.Location = new Point(x, y);
        }

        static int levels = 0;
        private void ApplyBackColorRecursive(Control parent, Color bg, Color fg)
        {
            try
            {
                parent.BackColor = bg;
                if (parent.Tag != "CurrentTemp") parent.ForeColor = fg;
            }
            catch { }

            foreach (Control child in parent.Controls)
            {
                ApplyBackColorRecursive(child, bg, fg);
            }
        }

        private void UpdateUiWithWeather(WeatherData data)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => UpdateUiWithWeather(data)));
                return;
            }

            _lblCurrentTemp.Text = $"{Math.Round(data.CurrentTempF):0}°F";
            if(Math.Round(data.CurrentTempF)< 50)
                _lblCurrentTemp.ForeColor = Color.FromArgb(150, 200, 255);
            else if (Math.Round(data.CurrentTempF) > 80 && Math.Round(data.CurrentTempF) < 100)
                _lblCurrentTemp.ForeColor = Color.FromArgb(255, 150, 150);
            else if (Math.Round(data.CurrentTempF) > 100)
                _lblCurrentTemp.ForeColor = Color.FromArgb(255, 0, 0);
            else
                _lblCurrentTemp.ForeColor = Color.FromArgb(200, 255, 150);
            

            _lblHighLow.Text = $"L: {Math.Round(data.TodayLowF):0}°F   H: {Math.Round(data.TodayHighF):0}°F";
            string windDir = double.IsNaN(data.WindDirectionDegrees)
                ? "--"
                : ToCompass(data.WindDirectionDegrees);

            string windText = $"{Math.Round(data.WindSpeedMph):0} mph {windDir}";
            string pressureText = $"{data.BarometricPressureInHg:0.00} in";

            _lblHumidity.Text =
                $"Humidity: {data.HumidityPercent}% Wind: {windText} Pressure: {pressureText}";

            if (data.Sunrise != DateTime.MinValue && data.Sunset != DateTime.MinValue)
            {
                _lblSunriseSunset.Text =
                    $"Sunrise: {data.Sunrise:hh\\:mm tt}   Sunset: {data.Sunset:hh\\:mm tt}";
            }
            else
            {
                _lblSunriseSunset.Text = "Sunrise: --:--   Sunset: --:--";
            }

            // ===== DAY/NIGHT BACKGROUND =====
            // The API doesn't explicitly say if it's currently day or night, but we can infer it based
            // on sunrise/sunset times. Whichever one is earlier is the "next" event. If sunrise is
            // earlier, then it's currently night (waiting for sunrise).
            DateTime dtNow = DateTime.Now;
            bool isNight = false;
            if (data.Sunrise < data.Sunset)
            {
                if( dtNow < data.Sunset && dtNow < data.Sunrise)
                    dtNow = dtNow.AddDays(1);   // handle weird time zone issues
                if(dtNow < data.Sunrise || dtNow > data.Sunset)
                    isNight = true;
                else
                    isNight = false;
            }
            else if (dtNow > data.Sunset)
                isNight = true;
            else if (dtNow > data.Sunrise)
                isNight = false;
            else
            {
                MessageBox.Show($"What got us here? Unexpected time scenario encountered from NWS. They reported Sunrise={data.Sunrise}, Sunset={data.Sunset}, it's now Now={dtNow}"); // What got us here?
                Application.Exit();
            }

            // Colors
            Color dayColor = Color.FromArgb(15, 40, 90);
            Color nightColor = Color.Black;

            // TODO SWITCH IMAGE HERE based on isNight
            Color bg = isNight ? nightColor : dayColor;
            double currentPop = data.HourlyRain.Count > 0
                ? data.HourlyRain[0].Probability * 100.0
                : 0;
            
            Color fg = Color.White;
            /*if( isNight )
            {
                if (currentPop > 60)
                    this.BackgroundImage = Image.FromFile("RainyNight.png");
                else if (currentPop > 30)
                    this.BackgroundImage = Image.FromFile("CloudyNight.png");
                else
                    this.BackgroundImage = Image.FromFile("ClearNight.png");

            }
            else
            {
                if (currentPop > 60)
                    this.BackgroundImage = Image.FromFile("RainyDay.png");
                else if (currentPop > 30)
                    this.BackgroundImage = Image.FromFile("CloudyDay.png");
                else
                {
                    this.BackgroundImage = Image.FromFile("SunnyDay.png");
                    fg = Color.Black;
                }
            }

            bg = Color.FromArgb(0, bg);   // add some transparency
            */
            ApplyBackColorRecursive(this, bg, fg);

            // Chart background
            _chartHourlyRain.BackColor = bg;
            _chartHourlyRain.ChartAreas[0].BackColor = bg;

            // Chart gridlines (dim at night)
            _chartHourlyRain.ChartAreas[0].AxisX.LineColor = fg;
            _chartHourlyRain.ChartAreas[0].AxisY.LineColor = fg;
            _chartHourlyRain.ChartAreas[0].AxisY.MajorGrid.LineColor =
                isNight ? Color.FromArgb(60, 60, 60) : Color.FromArgb(40, 80, 140);

            // Chart label colors
            _chartHourlyRain.ChartAreas[0].AxisX.LabelStyle.ForeColor = fg;
            _chartHourlyRain.ChartAreas[0].AxisY.LabelStyle.ForeColor = fg;

            // Chart title color
            _chartHourlyRain.ChartAreas[0].AxisY.TitleForeColor = fg;

            if (!string.IsNullOrEmpty(data.City) || !string.IsNullOrEmpty(data.State))
                _lblLocation.Text = $"{data.City}, {data.State}";
            else
                _lblLocation.Text = "Unknown location";

            _lvSevenDay.BeginUpdate();
            _lvSevenDay.Items.Clear();
            foreach (var d in data.Daily)
            {
                var item = new ListViewItem(d.Date.ToString("ddd MMM dd"));
                item.SubItems.Add(Capitalize(d.Summary));
                item.SubItems.Add($"{Math.Round(d.LowF):0}°F");     // Low first
                item.SubItems.Add($"{Math.Round(d.HighF):0}°F");    // High second
                item.SubItems.Add($"{Math.Round(d.ProbabilityOfPrecip):0}%");   // NEW
                _lvSevenDay.Items.Add(item);
            }
            _lvSevenDay.EndUpdate();

            var series = _chartHourlyRain.Series["Rain"];
            series.Points.Clear();

            int index = 0;

            foreach (var h in data.HourlyRain)
            {
                double value = h.Probability * 100.0;

                // Label logic
                string label = (index == 0)
                    ? "Now"
                    : h.Time.ToString("htt").ToLower();

                int i = series.Points.AddXY(label, value);
                var pt = series.Points[i];

                pt.AxisLabel = label;   // <-- THIS is the important line

                pt.ToolTip = $"{label}: {Math.Round(value):0}%";

                if (value < 30)
                    pt.Color = Color.FromArgb(150, 255, 200);
                else if (value <= 60)
                    pt.Color = Color.FromArgb(255, 150, 150);
                else
                    pt.Color = Color.FromArgb(255, 0, 0);

                index++;
            }
        }

        private static string Capitalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return s;
            if (s.Length == 1) return s.ToUpperInvariant();
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }
    }

    // ====== DATA MODELS ======
    public class WeatherData
    {
        public double CurrentTempF { get; set; }
        public double TodayHighF { get; set; }
        public double TodayLowF { get; set; }
        public int HumidityPercent { get; set; }
        public DateTime Sunrise { get; set; }
        public DateTime Sunset { get; set; }
        public string City { get; set; } = "";
        public string State { get; set; } = "";
        public List<DailyForecast> Daily { get; set; } = new List<DailyForecast>();
        public List<HourlyRainChance> HourlyRain { get; set; } = new List<HourlyRainChance>();
        public double WindSpeedMph { get; set; }
        public double WindDirectionDegrees { get; set; }
        public double BarometricPressureInHg { get; set; }
    }

    public class DailyForecast
    {
        public DateTime Date { get; set; }
        public double HighF { get; set; }
        public double LowF { get; set; }
        public string Summary { get; set; } = "";
        public double ProbabilityOfPrecip { get; set; }   // NEW
    }

    public class HourlyRainChance
    {
        public DateTime Time { get; set; }
        public double Probability { get; set; } // 0..1
    }
}
