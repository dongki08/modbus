using Modbus.Data;
using Modbus.Device;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Threading;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Controls.Primitives;

namespace modbus
{
    public partial class MainWindow : Window
    {
        private TcpListener tcpListener;
        private ModbusTcpSlave modbusSlave;
        private CustomDataStore customDataStore;
        private Dictionary<byte, ModbusSlaveDevice> slaveDevices = new Dictionary<byte, ModbusSlaveDevice>();
        private bool isServerRunning = false;
        private CancellationTokenSource cancellationTokenSource;

        public MainWindow()
        {
            InitializeComponent();
            customDataStore = new CustomDataStore();
        }

        private async void StartServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (isServerRunning)
                {
                    MessageBox.Show("서버가 이미 실행 중입니다.");
                    return;
                }

                IPAddress ipAddress = IPAddress.Parse(IpTextBox.Text);
                int port = int.Parse(PortTextBox.Text);

                tcpListener = new TcpListener(ipAddress, port);
                tcpListener.Start();

                // Modbus TCP Slave 생성
                modbusSlave = ModbusTcpSlave.CreateTcp(0, tcpListener);
                modbusSlave.DataStore = customDataStore;

                // 요청 수신 이벤트 등록 - 최적화됨
                modbusSlave.ModbusSlaveRequestReceived += (s, args) =>
                {
                    // 현재 장치 ID 설정 (필요한 경우에만 DataStore 로드)
                    byte requestedUnitId = args.Message.SlaveAddress;
                    if (customDataStore.CurrentUnitId != requestedUnitId)
                    {
                        customDataStore.SetCurrentUnitId(requestedUnitId);
                    }

                    // 상세한 요청 정보 로깅
                    string functionName = GetFunctionCodeName(args.Message.FunctionCode);
                    Log($"장치 {requestedUnitId}로부터 {functionName} 요청 수신");
                };

                // DataStore 이벤트 등록
                customDataStore.RegisterDataStoreEvents();
                customDataStore.SetSlaveDevices(slaveDevices);

                isServerRunning = true;
                cancellationTokenSource = new CancellationTokenSource();

                // 서버를 별도 스레드에서 실행
                _ = Task.Run(() => RunModbusServer(cancellationTokenSource.Token));

                ServerStatusText.Text = "서버 실행중";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Green;

                Log($"서버 시작됨 - {ipAddress}:{port}");
                MessageBox.Show("서버가 시작되었습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 시작 실패: {ex.Message}");
            }
        }

        private void StopServer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                isServerRunning = false;
                cancellationTokenSource?.Cancel();

                modbusSlave?.Dispose();
                modbusSlave = null;

                tcpListener?.Stop();
                tcpListener = null;

                ServerStatusText.Text = "서버 중지됨";
                ServerStatusText.Foreground = System.Windows.Media.Brushes.Red;

                Log("서버 중지됨");
                MessageBox.Show("서버가 중지되었습니다.");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"서버 중지 실패: {ex.Message}");
            }
        }

        private async Task RunModbusServer(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Run(() =>
                {
                    while (!cancellationToken.IsCancellationRequested && isServerRunning)
                    {
                        try
                        {
                            // NModbus4의 Listen()은 블로킹 방식으로 동작
                            modbusSlave?.Listen();

                            // 짧은 지연으로 CPU 사용률 조절
                            Thread.Sleep(1);
                        }
                        catch (Exception ex)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                Dispatcher.Invoke(() => Log($"서버 실행 오류: {ex.Message}"));
                            }
                            Thread.Sleep(100); // 오류 발생 시 잠시 대기
                        }
                    }
                }, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 정상적인 취소
                Dispatcher.Invoke(() => Log("서버 종료됨"));
            }
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            byte unitId;
            int count;

            if (!byte.TryParse(UnitIdTextBox.Text, out unitId) || unitId == 0)
            {
                MessageBox.Show("장치 ID를 1-255 사이의 값으로 입력하세요.");
                return;
            }

            if (slaveDevices.ContainsKey(unitId))
            {
                MessageBox.Show("이미 존재하는 장치입니다.");
                return;
            }

            if (!int.TryParse(AddressCountTextBox.Text, out count) || count <= 0)
            {
                MessageBox.Show("올바른 주소 수를 입력하세요.");
                return;
            }

            ComboBoxItem selectedItem = RegisterTypeComboBox.SelectedItem as ComboBoxItem;
            if (selectedItem == null)
            {
                MessageBox.Show("레지스터 유형을 선택하세요.");
                return;
            }

            string regType = selectedItem.Content.ToString().Substring(0, 2);
            ModbusSlaveDevice device = new ModbusSlaveDevice(unitId);

            switch (regType)
            {
                case "01":
                    device.InitializeCoils(count);
                    break;
                case "02":
                    device.InitializeDiscreteInputs(count);
                    break;
                case "03":
                    device.InitializeHoldingRegisters(count);
                    break;
                case "04":
                    device.InitializeInputRegisters(count);
                    break;
            }

            slaveDevices.Add(unitId, device);
            customDataStore.AddDevice(unitId, device);

            TabItem tab = new TabItem();
            tab.Header = $"장치 {unitId}";
            tab.Tag = unitId;
            tab.Content = CreateDeviceTab(device);
            DeviceTabControl.Items.Add(tab);
            DeviceTabControl.SelectedItem = tab;

            Log($"장치 {unitId} 추가됨 (유형: {selectedItem.Content})");
        }

        private void DeleteDevice_Click(object sender, RoutedEventArgs e)
        {
            TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
            if (selectedTab != null && selectedTab.Tag is byte)
            {
                byte unitId = (byte)selectedTab.Tag;
                DeviceTabControl.Items.Remove(selectedTab);
                slaveDevices.Remove(unitId);
                customDataStore.RemoveDevice(unitId);
                Log($"장치 {unitId} 삭제됨");
            }
            else
            {
                MessageBox.Show("삭제할 장치를 선택하세요.");
            }
        }

        private UIElement CreateDeviceTab(ModbusSlaveDevice device)
        {
            // Grid로 변경하여 높이 관리
            Grid mainGrid = new Grid();
            mainGrid.Margin = new Thickness(4);

            // 각 레지스터 타입별로 행 정의
            int rowCount = 0;
            if (device.Coils != null) rowCount++;
            if (device.DiscreteInputs != null) rowCount++;
            if (device.HoldingRegisters != null) rowCount++;
            if (device.InputRegisters != null) rowCount++;

            // 행 정의 - 균등 분할
            for (int i = 0; i < rowCount; i++)
            {
                mainGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            }

            int currentRow = 0;

            if (device.Coils != null)
            {
                var card = CreateRegisterCard("🔵 Coil [00001+]", device.Coils, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            if (device.DiscreteInputs != null)
            {
                var card = CreateRegisterCard("🟢 Input Status [10001+]", device.DiscreteInputs, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            if (device.HoldingRegisters != null)
            {
                var card = CreateRegisterCard("🟠 Holding Register [40001+]", device.HoldingRegisters, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            if (device.InputRegisters != null)
            {
                var card = CreateRegisterCard("🟡 Input Register [30001+]", device.InputRegisters, true);
                Grid.SetRow(card, currentRow++);
                mainGrid.Children.Add(card);
            }

            return mainGrid;
        }

        private UIElement CreateRegisterCard(string title, ObservableCollection<RegisterModel> data, bool fillHeight = false)
        {
            // 탭 컨트롤 높이에 맞는 카드 컨테이너
            Border cardBorder = new Border();
            cardBorder.Background = Brushes.White;
            cardBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(225, 225, 225));
            cardBorder.BorderThickness = new Thickness(1);
            cardBorder.CornerRadius = new CornerRadius(6);
            cardBorder.Margin = new Thickness(0, 0, 0, 4); // 작은 간격
            cardBorder.HorizontalAlignment = HorizontalAlignment.Stretch;
            cardBorder.VerticalAlignment = VerticalAlignment.Stretch; // 세로로 늘어남
            cardBorder.Effect = new DropShadowEffect
            {
                Color = Color.FromArgb(0x15, 0x00, 0x00, 0x00),
                BlurRadius = 6,
                ShadowDepth = 1,
                Opacity = 0.2
            };

            // 카드 내용 - Grid로 변경하여 높이 관리
            Grid cardContent = new Grid();
            cardContent.Margin = new Thickness(12, 8, 12, 8);
            cardContent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 헤더
            cardContent.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 그리드

            // 헤더
            TextBlock header = new TextBlock();
            header.Text = title;
            header.FontSize = 14;
            header.FontWeight = FontWeights.SemiBold;
            header.Foreground = new SolidColorBrush(Color.FromRgb(44, 44, 44));
            header.Margin = new Thickness(0, 0, 0, 8);
            Grid.SetRow(header, 0);
            cardContent.Children.Add(header);

            // 데이터 그리드 - 남은 공간 모두 사용
            DataGrid grid = new DataGrid();
            grid.ItemsSource = data;
            grid.IsReadOnly = false;
            grid.AutoGenerateColumns = false;
            grid.CanUserAddRows = false;
            grid.CanUserDeleteRows = false;
            grid.CanUserResizeColumns = false;
            grid.CanUserResizeRows = false;
            grid.Background = Brushes.Transparent;
            grid.BorderThickness = new Thickness(0);
            grid.GridLinesVisibility = DataGridGridLinesVisibility.Horizontal;
            grid.HorizontalGridLinesBrush = new SolidColorBrush(Color.FromRgb(245, 245, 245));
            grid.HeadersVisibility = DataGridHeadersVisibility.Column;
            grid.RowHeight = 28;
            grid.FontSize = 12;
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.VerticalAlignment = VerticalAlignment.Stretch;

            // 높이 설정 - 탭 컨트롤에 맞게 늘어남
            if (fillHeight)
            {
                // 부모 컨테이너 높이에 맞게 조정
                grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                // Height 설정 없음 - Grid의 Star로 자동 조정
            }
            else
            {
                // 기존 방식 유지
                if (data.Count <= 8)
                {
                    grid.Height = (data.Count + 1) * 30 + 5;
                    grid.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
                }
                else
                {
                    grid.Height = 250;
                    grid.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                }
            }

            Grid.SetRow(grid, 1);

            // 헤더 스타일
            grid.ColumnHeaderStyle = new Style(typeof(DataGridColumnHeader));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty,
                new SolidColorBrush(Color.FromRgb(248, 249, 250))));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty,
                new SolidColorBrush(Color.FromRgb(92, 92, 92))));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty,
                FontWeights.Medium));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 12.0));
            grid.ColumnHeaderStyle.Setters.Add(new Setter(DataGridColumnHeader.HeightProperty, 30.0));

            // 동적 컬럼 정의
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Address",
                Binding = new Binding("DisplayAddress"),
                IsReadOnly = true,
                Width = new DataGridLength(0.3, DataGridLengthUnitType.Star), // 30% 비율
                MinWidth = 100
            });

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new Binding("Value"),
                IsReadOnly = false,
                Width = new DataGridLength(0.35, DataGridLengthUnitType.Star), // 35% 비율
                MinWidth = 100
            });

            // 셀 편집 완료 이벤트
            grid.CellEditEnding += (sender, e) =>
            {
                if (e.EditAction == DataGridEditAction.Commit)
                {
                    var register = e.Row.Item as RegisterModel;
                    if (register != null)
                    {
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            Log($"레지스터 {register.DisplayAddress} 값이 {register.Value}로 변경됨");
                            UpdateCurrentDeviceDataStore();
                        }), System.Windows.Threading.DispatcherPriority.Background);
                    }
                }
            };

            cardContent.Children.Add(grid);
            cardBorder.Child = cardContent;
            return cardBorder;
        }

        // 현재 선택된 장치의 DataStore 업데이트 - UI 변경 시 호출
        private void UpdateCurrentDeviceDataStore()
        {
            // 마스터로부터의 업데이트 중이면 UI -> DataStore 업데이트 건너뛰기
            if (customDataStore.IsUpdatingFromMaster)
            {
                System.Diagnostics.Debug.WriteLine("마스터 업데이트 중 - UI 변경 무시");
                return;
            }

            TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
            if (selectedTab != null && selectedTab.Tag is byte)
            {
                byte unitId = (byte)selectedTab.Tag;
                if (slaveDevices.ContainsKey(unitId))
                {
                    var device = slaveDevices[unitId];

                    // UI 값을 DataStore에 즉시 반영 (한 방향만)
                    System.Diagnostics.Debug.WriteLine($"UI 변경 감지 - DataStore 업데이트 시작");
                    UpdateDataStoreFromUIChange(device);
                }
            }
        }

        // UI 변경사항을 DataStore에 반영 (단방향)
        private void UpdateDataStoreFromUIChange(ModbusSlaveDevice device)
        {
            try
            {
                customDataStore.SetCurrentUnitId(device.UnitId);

                // Holding Registers 업데이트
                if (device.HoldingRegisters != null)
                {
                    for (int i = 0; i < device.HoldingRegisters.Count && i + 1 < customDataStore.HoldingRegisters.Count; i++)
                    {
                        // 값 범위 체크 (0-65535)
                        int uiValueInt = device.HoldingRegisters[i].Value;
                        ushort uiValue = (ushort)Math.Max(0, Math.Min(65535, uiValueInt));
                        ushort dataStoreValue = customDataStore.HoldingRegisters[i + 1];

                        if (uiValue != dataStoreValue)
                        {
                            customDataStore.HoldingRegisters[i + 1] = uiValue;
                            System.Diagnostics.Debug.WriteLine($"UI → DataStore: HoldingRegister[{i + 1}] = {uiValue} (원본:{uiValueInt}, 표시:{device.HoldingRegisters[i].DisplayAddress})");
                        }
                    }
                }

                // Input Registers 업데이트 (시뮬레이터에서는 UI 변경 가능)
                if (device.InputRegisters != null)
                {
                    for (int i = 0; i < device.InputRegisters.Count && i + 1 < customDataStore.InputRegisters.Count; i++)
                    {
                        // 값 범위 체크 (0-65535)
                        int uiValueInt = device.InputRegisters[i].Value;
                        ushort uiValue = (ushort)Math.Max(0, Math.Min(65535, uiValueInt));
                        ushort dataStoreValue = customDataStore.InputRegisters[i + 1];

                        if (uiValue != dataStoreValue)
                        {
                            customDataStore.InputRegisters[i + 1] = uiValue;
                            System.Diagnostics.Debug.WriteLine($"UI → DataStore: InputRegister[{i + 1}] = {uiValue} (원본:{uiValueInt}, 표시:{device.InputRegisters[i].DisplayAddress})");
                        }
                    }
                }

                // Coils 업데이트
                if (device.Coils != null)
                {
                    for (int i = 0; i < device.Coils.Count && i + 1 < customDataStore.CoilDiscretes.Count; i++)
                    {
                        bool uiValue = device.Coils[i].Value != 0;
                        bool dataStoreValue = customDataStore.CoilDiscretes[i + 1];

                        if (uiValue != dataStoreValue)
                        {
                            customDataStore.CoilDiscretes[i + 1] = uiValue;
                            System.Diagnostics.Debug.WriteLine($"UI → DataStore: Coil[{i + 1}] = {uiValue} (표시:{device.Coils[i].DisplayAddress})");
                        }
                    }
                }

                // Discrete Inputs 업데이트 (시뮬레이터에서는 UI 변경 가능)
                if (device.DiscreteInputs != null)
                {
                    for (int i = 0; i < device.DiscreteInputs.Count && i + 1 < customDataStore.InputDiscretes.Count; i++)
                    {
                        bool uiValue = device.DiscreteInputs[i].Value != 0;
                        bool dataStoreValue = customDataStore.InputDiscretes[i + 1];

                        if (uiValue != dataStoreValue)
                        {
                            customDataStore.InputDiscretes[i + 1] = uiValue;
                            System.Diagnostics.Debug.WriteLine($"UI → DataStore: DiscreteInput[{i + 1}] = {uiValue} (표시:{device.DiscreteInputs[i].DisplayAddress})");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UI → DataStore 업데이트 오류: {ex.Message}");
            }
        }

        private void UpdateDataStoreFromCurrentDevice(ModbusSlaveDevice device)
        {
            if (customDataStore == null) return;

            try
            {
                // Input Registers 특별 처리 - 패딩 없이 직접 매칭
                if (device.InputRegisters != null)
                {
                    customDataStore.InputRegisters.Clear();
                    // 마스터 주소 0 = UI의 30001 (첫 번째 항목)
                    for (int i = 0; i < device.InputRegisters.Count; i++)
                    {
                        customDataStore.InputRegisters.Add((ushort)device.InputRegisters[i].Value);
                    }

                    Log($"Input Registers 업데이트됨: {device.InputRegisters.Count}개 (마스터 주소 0 = 30001)");
                }

                // Holding Registers - 패딩 없이 직접 매칭
                if (device.HoldingRegisters != null)
                {
                    customDataStore.HoldingRegisters.Clear();
                    // 마스터 주소 0 = UI의 40001 (첫 번째 항목)
                    for (int i = 0; i < device.HoldingRegisters.Count; i++)
                    {
                        customDataStore.HoldingRegisters.Add((ushort)device.HoldingRegisters[i].Value);
                    }
                }

                // Coils - 패딩 없이 직접 매칭
                if (device.Coils != null)
                {
                    customDataStore.CoilDiscretes.Clear();
                    // 마스터 주소 0 = UI의 00001 (첫 번째 항목)
                    for (int i = 0; i < device.Coils.Count; i++)
                    {
                        customDataStore.CoilDiscretes.Add(device.Coils[i].Value != 0);
                    }
                }

                // Discrete Inputs - 패딩 없이 직접 매칭
                if (device.DiscreteInputs != null)
                {
                    customDataStore.InputDiscretes.Clear();
                    // 마스터 주소 0 = UI의 10001 (첫 번째 항목)
                    for (int i = 0; i < device.DiscreteInputs.Count; i++)
                    {
                        customDataStore.InputDiscretes.Add(device.DiscreteInputs[i].Value != 0);
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"DataStore 업데이트 오류: {ex.Message}");
            }
        }

        private void TestAddressMapping_Click(object sender, RoutedEventArgs e)
        {
            TabItem selectedTab = DeviceTabControl.SelectedItem as TabItem;
            if (selectedTab != null && selectedTab.Tag is byte)
            {
                byte unitId = (byte)selectedTab.Tag;
                if (slaveDevices.ContainsKey(unitId))
                {
                    var device = slaveDevices[unitId];

                    Log("=== 주소 매칭 테스트 시작 ===");
                    Log($"현재 장치: {unitId}");
                    Log($"DataStore HoldingRegisters 개수: {customDataStore.HoldingRegisters.Count}");
                    Log($"UI HoldingRegisters 개수: {device.HoldingRegisters?.Count ?? 0}");

                    if (device.HoldingRegisters != null)
                    {
                        for (int i = 0; i < Math.Min(5, device.HoldingRegisters.Count); i++)
                        {
                            int dataStoreValue = customDataStore.HoldingRegisters.Count > i ? customDataStore.HoldingRegisters[i] : -1;
                            int uiValue = device.HoldingRegisters[i].Value;
                            int displayAddress = device.HoldingRegisters[i].DisplayAddress;

                            Log($"인덱스 {i}: DataStore={dataStoreValue}, UI={uiValue}, 표시주소={displayAddress}");
                        }
                    }

                    Log("=== 주소 매칭 테스트 완료 ===");
                }
            }
        }

        private string GetFunctionCodeName(byte functionCode)
        {
            switch (functionCode)
            {
                case 1: return "Read Coils";
                case 2: return "Read Discrete Inputs";
                case 3: return "Read Holding Registers";
                case 4: return "Read Input Registers";
                case 5: return "Write Single Coil";
                case 6: return "Write Single Register";
                case 15: return "Write Multiple Coils";
                case 16: return "Write Multiple Registers";
                default: return $"Unknown Function Code {functionCode}";
            }
        }

        private void Log(string message)
        {
            Dispatcher.Invoke(() =>
            {
                LogBox.Items.Add($"{DateTime.Now:HH:mm:ss} - {message}");
                if (LogBox.Items.Count > 100)
                    LogBox.Items.RemoveAt(0);
                LogBox.ScrollIntoView(LogBox.Items[LogBox.Items.Count - 1]);
            });
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (isServerRunning)
            {
                StopServer_Click(null, null);
            }
            base.OnClosing(e);
        }
    }

    // RegisterModel 클래스
    public class RegisterModel : INotifyPropertyChanged
    {
        private int _value;
        public int DisplayAddress { get; set; }      // 사용자에게 보여주는 주소 (30001, 40001 등)
        public int ModbusAddress { get; set; }       // 실제 Modbus 프로토콜 주소 (0-based)

        public int Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    int oldValue = _value;
                    _value = value;

                    // PropertyChanged 이벤트 발생 (UI 바인딩용)
                    OnPropertyChanged(nameof(Value));

                    // ValueChanged 이벤트 발생 (사용자 정의 이벤트)
                    ValueChanged?.Invoke(this, EventArgs.Empty);

                    // 디버그 로그
                    System.Diagnostics.Debug.WriteLine($"RegisterModel 값 변경 - Display:{DisplayAddress} Modbus:{ModbusAddress} : {oldValue} -> {value}");
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        public event EventHandler ValueChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // ModbusSlaveDevice 클래스
    public class ModbusSlaveDevice
    {
        public byte UnitId { get; private set; }

        public ObservableCollection<RegisterModel> Coils;
        public ObservableCollection<RegisterModel> DiscreteInputs;
        public ObservableCollection<RegisterModel> HoldingRegisters;
        public ObservableCollection<RegisterModel> InputRegisters;

        public ModbusSlaveDevice(byte unitId)
        {
            UnitId = unitId;
        }

        public void InitializeCoils(int count)
        {
            Coils = CreateRegisters(count, 1);
        }

        public void InitializeDiscreteInputs(int count)
        {
            DiscreteInputs = CreateRegisters(count, 10001);
        }

        public void InitializeHoldingRegisters(int count)
        {
            HoldingRegisters = CreateRegisters(count, 40001);
        }

        public void InitializeInputRegisters(int count)
        {
            InputRegisters = CreateRegisters(count, 30001);
        }

        private ObservableCollection<RegisterModel> CreateRegisters(int count, int baseAddr)
        {
            ObservableCollection<RegisterModel> list = new ObservableCollection<RegisterModel>();
            for (int i = 0; i < count; i++)
            {
                // DisplayAddress는 사용자에게 보여주는 주소 (1-based)
                // 실제 Modbus 주소는 0-based이므로 내부적으로는 i를 사용
                var register = new RegisterModel
                {
                    DisplayAddress = baseAddr + i,  // 표시용 주소
                    ModbusAddress = i,              // 실제 Modbus 주소 (0-based)
                    Value = 0
                };

                // 각 레지스터의 값 변경 이벤트 구독
                register.ValueChanged += (sender, e) =>
                {
                    System.Diagnostics.Debug.WriteLine($"Device {UnitId} Register {register.DisplayAddress} (Modbus: {register.ModbusAddress}) changed to {register.Value}");
                };

                list.Add(register);
            }
            return list;
        }
    }

    // CustomDataStore 클래스
    public class CustomDataStore : DataStore
    {
        private Dictionary<byte, ModbusSlaveDevice> devices = new Dictionary<byte, ModbusSlaveDevice>();
        private byte currentUnitId = 1;
        private bool isUpdatingFromMaster = false; // 순환 업데이트 방지 플래그

        public bool IsUpdatingFromMaster => isUpdatingFromMaster;
        public byte CurrentUnitId => currentUnitId;

        public CustomDataStore() : base()
        {
            // 기본 DataStore는 이미 컬렉션들을 초기화합니다
            // CoilDiscretes, InputDiscretes, HoldingRegisters, InputRegisters
        }

        public void SetSlaveDevices(Dictionary<byte, ModbusSlaveDevice> slaveDevices)
        {
            devices = slaveDevices;
        }

        public void AddDevice(byte unitId, ModbusSlaveDevice device)
        {
            devices[unitId] = device;

            // 장치 추가 시 UI의 기존 값을 DataStore에 로드
            LoadUIValuesToDataStore(device);
        }

        public void RemoveDevice(byte unitId)
        {
            devices.Remove(unitId);
        }

        public void SetCurrentUnitId(byte unitId)
        {
            if (currentUnitId == unitId) return; // 동일한 장치면 처리하지 않음

            byte previousUnitId = currentUnitId;
            currentUnitId = unitId;
            System.Diagnostics.Debug.WriteLine($"★★★ 장치 전환: {previousUnitId} → {unitId}");

            // 장치 전환 시 해당 장치의 UI 값을 DataStore에 로드
            if (devices.ContainsKey(unitId))
            {
                System.Diagnostics.Debug.WriteLine($"장치 {unitId}의 데이터를 DataStore에 로드");
                LoadUIValuesToDataStore(devices[unitId]);
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"경고: 장치 {unitId}를 찾을 수 없음");
            }
        }

        // UI의 기존 값들을 DataStore에 로드 (UI 값 보존)
        private void LoadUIValuesToDataStore(ModbusSlaveDevice device)
        {
            System.Diagnostics.Debug.WriteLine($"=== 장치 {device.UnitId} UI 값을 DataStore로 로드 시작 ===");

            // Holding Registers - UI 값을 DataStore에 복사
            if (device.HoldingRegisters != null)
            {
                this.HoldingRegisters.Clear();
                //this.HoldingRegisters.Add(0); // 0번 인덱스 패딩

                for (int i = 0; i < device.HoldingRegisters.Count; i++)
                {
                    // 값 범위 체크 (0-65535)
                    int uiValueInt = device.HoldingRegisters[i].Value;
                    ushort uiValue = (ushort)Math.Max(0, Math.Min(65535, uiValueInt));
                    this.HoldingRegisters.Add(uiValue);

                    if (uiValueInt != uiValue)
                    {
                        System.Diagnostics.Debug.WriteLine($"값 범위 조정: {uiValueInt} → {uiValue}");
                    }

                    System.Diagnostics.Debug.WriteLine($"UI → DataStore: HoldingRegister[{i}] (표시:{device.HoldingRegisters[i].DisplayAddress}) = {uiValue}");
                }
                System.Diagnostics.Debug.WriteLine($"HoldingRegisters 로드 완료 - DataStore 크기: {this.HoldingRegisters.Count}");
            }

            // Input Registers - UI 값을 DataStore에 복사 (시뮬레이터에서 중요!)
            if (device.InputRegisters != null)
            {
                this.InputRegisters.Clear();
                //this.InputRegisters.Add(0); // 0번 인덱스 패딩

                for (int i = 0; i < device.InputRegisters.Count; i++)
                {
                    // 값 범위 체크 (0-65535)
                    int uiValueInt = device.InputRegisters[i].Value;
                    ushort uiValue = (ushort)Math.Max(0, Math.Min(65535, uiValueInt));
                    this.InputRegisters.Add(uiValue);

                    if (uiValueInt != uiValue)
                    {
                        System.Diagnostics.Debug.WriteLine($"값 범위 조정: {uiValueInt} → {uiValue}");
                    }

                    System.Diagnostics.Debug.WriteLine($"UI → DataStore: InputRegister[{i}] (표시:{device.InputRegisters[i].DisplayAddress}) = {uiValue}");
                }

                System.Diagnostics.Debug.WriteLine($"★★★ InputRegisters 로드 완료 - DataStore 크기: {this.InputRegisters.Count}");
            }

            // Coils - UI 값을 DataStore에 복사
            if (device.Coils != null)
            {
                this.CoilDiscretes.Clear();

                for (int i = 0; i < device.Coils.Count; i++)
                {
                    bool uiValue = device.Coils[i].Value != 0;
                    this.CoilDiscretes.Add(uiValue);
                    System.Diagnostics.Debug.WriteLine($"UI → DataStore: Coil[{i}] (표시:{device.Coils[i].DisplayAddress}) = {uiValue}");
                }
            }

            // Discrete Inputs - UI 값을 DataStore에 복사
            if (device.DiscreteInputs != null)
            {
                this.InputDiscretes.Clear();

                for (int i = 0; i < device.DiscreteInputs.Count; i++)
                {
                    bool uiValue = device.DiscreteInputs[i].Value != 0;
                    this.InputDiscretes.Add(uiValue);
                    System.Diagnostics.Debug.WriteLine($"UI → DataStore: DiscreteInput[{i}] (표시:{device.DiscreteInputs[i].DisplayAddress}) = {uiValue}");
                }
            }

            System.Diagnostics.Debug.WriteLine($"=== 장치 {device.UnitId} UI 값을 DataStore로 로드 완료 ===");
        }

        private void UpdateDataStoreFromDevice(ModbusSlaveDevice device)
        {
            System.Diagnostics.Debug.WriteLine($"=== UpdateDataStoreFromDevice 시작 - 장치 {device.UnitId} ===");

            // Holding Registers 업데이트 - 기존 UI 값을 정확한 위치에 유지
            if (device.HoldingRegisters != null)
            {
                // 기존 DataStore 값들 백업 (마스터가 이미 쓴 값들 보존)
                var existingValues = new Dictionary<int, ushort>();
                for (int i = 1; i < this.HoldingRegisters.Count; i++)
                {
                    existingValues[i] = this.HoldingRegisters[i];
                }

                this.HoldingRegisters.Clear();

                System.Diagnostics.Debug.WriteLine($"HoldingRegisters 초기화 - UI 데이터 {device.HoldingRegisters.Count}개");

                for (int i = 0; i < device.HoldingRegisters.Count; i++)
                {
                    ushort value;

                    // 마스터가 이미 변경한 값이 있으면 그 값을 사용, 없으면 UI 값 사용
                    if (existingValues.ContainsKey(i + 1))
                    {
                        value = existingValues[i + 1];
                        System.Diagnostics.Debug.WriteLine($"마스터 값 유지 - DataStore[{i + 1}] = {value} (UI 인덱스: {i}, 표시주소: {device.HoldingRegisters[i].DisplayAddress})");
                    }
                    else
                    {
                        value = (ushort)device.HoldingRegisters[i].Value;
                        System.Diagnostics.Debug.WriteLine($"UI 값 사용 - DataStore[{i + 1}] = {value} (UI 인덱스: {i}, 표시주소: {device.HoldingRegisters[i].DisplayAddress})");
                    }

                    this.HoldingRegisters.Add(value);

                    // UI도 DataStore 값과 동기화 (메인 스레드에서 실행)
                    if (device.HoldingRegisters[i].Value != value)
                    {
                        int index = i; // 클로저를 위한 로컬 변수
                        ushort newValue = value;

                        // UI 업데이트를 메인 스레드에서 실행
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                device.HoldingRegisters[index].Value = newValue;
                                System.Diagnostics.Debug.WriteLine($"UI 동기화 완료 - UI[{index}] 값을 {newValue}로 업데이트");
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"UI 업데이트 오류: {ex.Message}");
                            }
                        }));
                    }
                }

                System.Diagnostics.Debug.WriteLine($"최종 DataStore 크기: {this.HoldingRegisters.Count} (패딩 포함)");
            }

            // Coils도 동일한 방식으로 처리
            if (device.Coils != null)
            {
                var existingValues = new Dictionary<int, bool>();
                for (int i = 1; i < this.CoilDiscretes.Count; i++)
                {
                    existingValues[i] = this.CoilDiscretes[i];
                }

                this.CoilDiscretes.Clear();

                for (int i = 0; i < device.Coils.Count; i++)
                {
                    bool value;

                    if (existingValues.ContainsKey(i + 1))
                    {
                        value = existingValues[i + 1];
                    }
                    else
                    {
                        value = device.Coils[i].Value != 0;
                    }

                    this.CoilDiscretes.Add(value);

                    if (device.Coils[i].Value != (value ? 1 : 0))
                    {
                        int index = i;
                        int newValue = value ? 1 : 0;

                        System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            device.Coils[index].Value = newValue;
                        }));
                    }
                }
            }

            // Input 계열은 읽기 전용이므로 UI 값 그대로 사용
            if (device.DiscreteInputs != null)
            {
                this.InputDiscretes.Clear();

                for (int i = 0; i < device.DiscreteInputs.Count; i++)
                {
                    this.InputDiscretes.Add(device.DiscreteInputs[i].Value != 0);
                }
            }

            if (device.InputRegisters != null)
            {
                this.InputRegisters.Clear();

                for (int i = 0; i < device.InputRegisters.Count; i++)
                {
                    this.InputRegisters.Add((ushort)device.InputRegisters[i].Value);
                }
            }

            System.Diagnostics.Debug.WriteLine($"=== UpdateDataStoreFromDevice 완료 ===");
        }

        private void UpdateDeviceFromDataStore(ModbusSlaveDevice device)
        {
            // DataStore의 변경사항을 다시 장치로 복사 - 패딩 없이 직접 매칭
            if (device.Coils != null && this.CoilDiscretes.Count > 0)
            {
                for (int i = 0; i < Math.Min(device.Coils.Count, this.CoilDiscretes.Count); i++)
                {
                    device.Coils[i].Value = this.CoilDiscretes[i] ? 1 : 0;
                }
            }

            if (device.HoldingRegisters != null && this.HoldingRegisters.Count > 0)
            {
                for (int i = 0; i < Math.Min(device.HoldingRegisters.Count, this.HoldingRegisters.Count); i++)
                {
                    device.HoldingRegisters[i].Value = this.HoldingRegisters[i];
                }
            }

            // Input Registers도 업데이트 가능하도록 추가 (시뮬레이터용) - 패딩 없이 직접 매칭
            if (device.InputRegisters != null && this.InputRegisters.Count > 0)
            {
                for (int i = 0; i < Math.Min(device.InputRegisters.Count, this.InputRegisters.Count); i++)
                {
                    device.InputRegisters[i].Value = this.InputRegisters[i];
                }
            }

            // Discrete Inputs도 업데이트 가능하도록 추가 (시뮬레이터용) - 패딩 없이 직접 매칭
            if (device.DiscreteInputs != null && this.InputDiscretes.Count > 0)
            {
                for (int i = 0; i < Math.Min(device.DiscreteInputs.Count, this.InputDiscretes.Count); i++)
                {
                    device.DiscreteInputs[i].Value = this.InputDiscretes[i] ? 1 : 0;
                }
            }
        }

        // DataStore가 변경될 때 호출되는 이벤트 핸들러 등록
        public void RegisterDataStoreEvents()
        {
            // 타이머 동기화 완전 제거 - 이벤트 기반만 사용
            this.DataStoreWrittenTo += OnDataStoreWrittenTo;
        }

        private void OnDataStoreWrittenTo(object sender, DataStoreEventArgs e)
        {
            // 마스터 쓰기 완료 후에만 UI 업데이트
            isUpdatingFromMaster = true;

            try
            {
                if (devices.ContainsKey(currentUnitId))
                {
                    var device = devices[currentUnitId];
                    System.Diagnostics.Debug.WriteLine($"마스터 쓰기 완료 - 장치 {currentUnitId}, 시작주소: {e.StartAddress}, 타입: {e.ModbusDataType}");

                    // 마스터가 쓴 값만 UI에 반영 (전체 동기화 아님)
                    UpdateUIFromMasterWrite(device, e);
                }
            }
            finally
            {
                isUpdatingFromMaster = false;
            }
        }

        // 마스터가 쓴 특정 값만 UI에 반영
        private void UpdateUIFromMasterWrite(ModbusSlaveDevice device, DataStoreEventArgs e)
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (e.ModbusDataType == ModbusDataType.HoldingRegister && device.HoldingRegisters != null)
                    {
                        // StartAddress부터 DataStore 값을 UI에 반영
                        int startIndex = e.StartAddress;
                        int dataStoreIndex = startIndex + 1; // DataStore는 1-based

                        if (startIndex < device.HoldingRegisters.Count && dataStoreIndex < this.HoldingRegisters.Count)
                        {
                            ushort newValue = this.HoldingRegisters[dataStoreIndex];
                            int oldValue = device.HoldingRegisters[startIndex].Value;

                            if (oldValue != newValue)
                            {
                                device.HoldingRegisters[startIndex].Value = newValue;
                                System.Diagnostics.Debug.WriteLine($"마스터 쓰기 반영 - UI[{startIndex}] (표시:{device.HoldingRegisters[startIndex].DisplayAddress}): {oldValue} → {newValue}");
                            }
                        }
                    }
                    else if (e.ModbusDataType == ModbusDataType.Coil && device.Coils != null)
                    {
                        int startIndex = e.StartAddress;
                        int dataStoreIndex = startIndex + 1;

                        if (startIndex < device.Coils.Count && dataStoreIndex < this.CoilDiscretes.Count)
                        {
                            bool newValue = this.CoilDiscretes[dataStoreIndex];
                            int oldValue = device.Coils[startIndex].Value;
                            int expectedValue = newValue ? 1 : 0;

                            if (oldValue != expectedValue)
                            {
                                device.Coils[startIndex].Value = expectedValue;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"UI 업데이트 오류: {ex.Message}");
                }
            }));
        }
    }
}