using System.IO;
using System.IO.Ports;
using System.Text;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Wp_serialPort;

public partial class MainWindow : Window
{
    private SerialPort? _serialPort;
    private long _receivedBytes;
    private bool _closing;
    private string cliInputBuffer = string.Empty;
    private int cliInputCursor;
    private bool _pendingCarriageReturn;
    private bool _pendingDetectedCarriageReturn;
    private bool _endLineDetected;
    private readonly DispatcherTimer _carriageReturnDetectionTimer;
    private bool _settingsCollapsed;
    private bool _commandsCollapsed;
    private bool _loadingCommandFiles;
    private bool _updatingSystemFields;
    private bool _batchSending;
    private readonly List<SystemEntry> _systemEntries = new();
    private readonly LocalCommandAdvisor _commandAdvisor = new();
    private ComboBox? _deviceTypeCombo;
    private string ConfigDirectory => Path.Combine(AppContext.BaseDirectory, "config");
    private string DefineDirectory => Path.Combine(AppContext.BaseDirectory, "define");
    private readonly List<(string Token, Func<string> GetValue)> _definitionBindings = new();

    public MainWindow()
    {
        InitializeComponent();
        _carriageReturnDetectionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _carriageReturnDetectionTimer.Tick += (_, _) =>
        {
            _carriageReturnDetectionTimer.Stop();
            if (_pendingDetectedCarriageReturn)
            {
                _pendingDetectedCarriageReturn = false;
                SetDetectedEndLine("CR");
            }
        };
        GatewayBlocksCombo.SelectedIndex = 2;
        RefreshPorts();
        PrepareApplicationFolders();
        LoadCommandFileIndex();
        LoadDefaultDefinitions();
        LoadDefaultSystemList();
        RefreshCommandSuggestions();
        Loaded += (_, _) =>
        {
            LogBox.CaretIndex = LogBox.Text.Length;
        };
        Closing += (_, _) =>
        {
            _closing = true;
            ClosePort();
        };
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e) => RefreshPorts();

    private void ToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsCollapsed = !_settingsCollapsed;
        SettingsColumn.Width = new GridLength(_settingsCollapsed ? 0 : 310);
        SettingsGapColumn.Width = new GridLength(_settingsCollapsed ? 0 : 20);
        SettingsToggleButton.Content = _settingsCollapsed ? "Ayarları göster ▶" : "Ayarları gizle ◀";
    }

    private void ToggleCommands_Click(object sender, RoutedEventArgs e)
    {
        _commandsCollapsed = !_commandsCollapsed;
        CommandsColumn.Width = new GridLength(_commandsCollapsed ? 0 : 300);
        CommandsGapColumn.Width = new GridLength(_commandsCollapsed ? 0 : 20);
        CommandsToggleButton.Content = _commandsCollapsed ? "Komutları göster ◀" : "Komutları gizle ▶";
    }

    private void PrepareApplicationFolders()
    {
        Directory.CreateDirectory(DefineDirectory);
        Directory.CreateDirectory(ConfigDirectory);

        EnsureExampleFile(
            Path.Combine(DefineDirectory, "data.txt"),
            Path.Combine(AppContext.BaseDirectory, "defines", "data.txt"),
            new[]
            {
                "val1_lb=Parametre 1|ornek1>ornek2|0",
                "val2_lb=Parametre 2|deger1>deger2|0",
                "val3_lb=Parametre 3|deger1>deger2|0",
                "val4_lb=Parametre 4|deger1>deger2|0",
                "val5_lb=Parametre 5|deger1>deger2|0",
                "val6_lb=Parametre 6|deger1>deger2|0",
                "val7_lb=Parametre 7|deger1>deger2|0",
                "val8_lb=Parametre 8|deger1>deger2|0",
                "devices_lb=Cihaz|ALC>HUA>ZTE|0"
            });
        EnsureExampleFile(
            Path.Combine(DefineDirectory, "syslist.txt"),
            Path.Combine(AppContext.BaseDirectory, "defines", "syslist.txt"),
            new[]
            {
                "ALC Demo (OLT-ALC-01|OLT-ALC-02) [192.0.2.10|192.0.2.11]",
                "HUA Demo (OLT-HUA-01) [198.51.100.10]",
                "ZTE Demo (OLT-ZTE-01) [203.0.113.10]"
            });
    }

    private static void EnsureExampleFile(string destination, string legacyPath, IEnumerable<string> sampleLines)
    {
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(legacyPath))
            File.Copy(legacyPath, destination);
        else
            File.WriteAllLines(destination, sampleLines, Encoding.UTF8);
    }

    private void LoadCommandFileIndex()
    {
        _loadingCommandFiles = true;
        try
        {
            CommandFilesList.Items.Clear();
            Directory.CreateDirectory(ConfigDirectory);
            foreach (var path in Directory.GetFiles(ConfigDirectory, "*.txt")
                         .Where(path => !string.Equals(Path.GetFileName(path), "filelist.txt", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                CommandFilesList.Items.Add(path);

            if (CommandFilesList.Items.Count > 0) CommandFilesList.SelectedIndex = 0;
        }
        catch (Exception ex)
        {
            AppendLog($"Komut dosyaları listesi yüklenemedi: {ex.Message}");
        }
        finally { _loadingCommandFiles = false; }

        if (CommandFilesList.SelectedItem is string selectedPath) LoadCommandFile(selectedPath);
        RefreshCommandSuggestions();
    }

    private void AddCommandFile(string path)
    {
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".txt", StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            var fullPath = Path.GetFullPath(path);
            var destination = Path.Combine(ConfigDirectory, Path.GetFileName(fullPath));
            if (!string.Equals(fullPath, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                var baseName = Path.GetFileNameWithoutExtension(destination);
                var extension = Path.GetExtension(destination);
                var suffix = 2;
                while (File.Exists(destination))
                    destination = Path.Combine(ConfigDirectory, $"{baseName} ({suffix++}){extension}");
                File.Copy(fullPath, destination);
            }

            LoadCommandFileIndex();
            CommandFilesList.SelectedItem = destination;
        }
        catch (Exception ex) { AppendLog($"TXT komut dosyası config klasörüne kopyalanamadı: {ex.Message}"); }
    }

    private void OpenCommandFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "TXT komut dosyası seç",
            Filter = "Metin dosyaları (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog() != true) return;
        foreach (var path in dialog.FileNames) AddCommandFile(path);
    }

    private void ReloadCommandFile_Click(object sender, RoutedEventArgs e)
    {
        var selectedPath = CommandFilesList.SelectedItem as string;
        LoadCommandFileIndex();
        if (selectedPath is not null && File.Exists(selectedPath)) CommandFilesList.SelectedItem = selectedPath;
        if (CommandFilesList.SelectedItem is string path) LoadCommandFile(path);
        else AppendLog("Config klasöründe TXT komut dosyası yok. TXT aç düğmesiyle bir dosya ekleyin.");
    }

    private void CommandFilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingCommandFiles && CommandFilesList.SelectedItem is string path)
            LoadCommandFile(path);
    }

    private void LoadCommandFile(string path)
    {
        try
        {
            CommandList.ItemsSource = File.ReadAllLines(path);
        }
        catch (Exception ex)
        {
            CommandList.ItemsSource = null;
            AppendLog($"Komut dosyası okunamadı ({Path.GetFileName(path)}): {ex.Message}");
        }
    }

    private void CommandFilesList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void CommandFilesList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            foreach (var path in paths) AddCommandFile(path);
    }

    private void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => SendSelectedCommand();

    private void SendSelectedCommand_Click(object sender, RoutedEventArgs e) => SendSelectedCommand();

    private void CommandList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (ItemsControl.ContainerFromElement(CommandList, source) is ListBoxItem item)
            item.IsSelected = true;
    }

    private async void SendFromSelectedRow_Click(object sender, RoutedEventArgs e)
    {
        if (CommandList.SelectedIndex < 0)
        {
            AppendLog("Toplu gönderim için önce bir komut satırı seçin.");
            return;
        }
        await SendCommandRangeAsync(CommandList.SelectedIndex);
    }

    private async void SendAllCommands_Click(object sender, RoutedEventArgs e)
    {
        if (CommandList.Items.Count <= 3)
        {
            AppendLog("İlk üç satır atlandığında gönderilecek komut kalmıyor.");
            return;
        }
        await SendCommandRangeAsync(3);
    }

    private async Task SendCommandRangeAsync(int startIndex)
    {
        if (_batchSending) return;
        if (string.IsNullOrWhiteSpace(SystemIpValueBox.Text))
        {
            MessageBox.Show("Toplu komut göndermeden önce Tanımlar sekmesindeki Sistem IP alanına bir IP adresi girin.", "IP adresi gerekli", MessageBoxButton.OK, MessageBoxImage.Warning);
            SidebarTabs.SelectedItem = DefinitionsTab;
            SystemIpValueBox.Focus();
            SystemIpValueBox.SelectAll();
            return;
        }
        if (_serialPort?.IsOpen != true)
        {
            AppendLog("Seri port bağlantısı kapalı; toplu komut gönderilemedi.");
            return;
        }

        _batchSending = true;
        var sentCount = 0;
        try
        {
            for (var index = startIndex; index < CommandList.Items.Count; index++)
            {
                if (_serialPort?.IsOpen != true)
                {
                    AppendLog("Bağlantı kesildi; toplu komut gönderimi durduruldu.");
                    break;
                }

                CommandList.SelectedIndex = index;
                CommandList.ScrollIntoView(CommandList.SelectedItem);
                try
                {
                    SendCommandLine(CommandList.Items[index]?.ToString() ?? string.Empty);
                    sentCount++;
                }
                catch (Exception ex)
                {
                    AppendLog($"{index + 1}. satır gönderilemedi; gönderim durduruldu: {ex.Message}");
                    break;
                }

                await Task.Yield();
            }

            if (sentCount > 0)
                MessageBox.Show($"Toplu gönderim tamamlandı. Gönderilen komut: {sentCount}.", "Gönderim tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshCommandSuggestions();
        }
        finally { _batchSending = false; }
    }

    private void SendSelectedCommand()
    {
        if (CommandList.SelectedItem is not string rawCommand)
        {
            AppendLog("Göndermek için hazır komut seçin.");
            return;
        }
        try
        {
            SendCommandLine(rawCommand);
            RefreshCommandSuggestions();
        }
        catch (Exception ex) { AppendLog($"Hazır komut gönderilemedi: {ex.Message}"); }
    }

    private void SendCommandLine(string rawCommand)
    {
        if (_serialPort?.IsOpen != true)
            throw new InvalidOperationException("Seri port bağlantısı kapalı.");

        var command = ReplaceCommandData(rawCommand).Split(new[] { "//" }, 2, StringSplitOptions.None)[0].TrimEnd();
        var lineEnding = EndLineCombo.SelectedValue?.ToString() switch
        {
            "CRLF" => "\r\n",
            "LF" => "\n",
            "CR" => "\r",
            _ => string.Empty
        };
        _serialPort.Write(command);
        if (lineEnding.Length > 0) _serialPort.Write(lineEnding);
        _commandAdvisor.RecordSent(CurrentDeviceType, rawCommand);
    }

    private string ReplaceCommandData(string command)
    {
        var replacements = new List<(string Token, string Value)>();
        var systemIp = SystemIpValueBox.Text;
        var routeIp = RouteIpValueBox.Text;

        AddToken(replacements, SystemIpTokenBox.Text, systemIp);
        AddToken(replacements, RouteIpTokenBox.Text, routeIp);
        foreach (var alias in new[] { "sysip", "sys_ip", "sys ip", "systemip", "system_ip", "System IP", "SYS IP", "Sistem IP", "Sistem IP Adresi" })
            AddToken(replacements, alias, systemIp);
        foreach (var alias in new[] { "routeip", "route_ip", "route ip", "gatewayip", "gateway_ip", "gateway ip", "Route IP", "Gateway IP", "Ağ geçidi IP", "Gateway IP Adresi" })
            AddToken(replacements, alias, routeIp);

        foreach (var binding in _definitionBindings)
            AddToken(replacements, binding.Token, binding.GetValue());

        foreach (var replacement in replacements.OrderByDescending(item => item.Token.Length))
        {
            command = command
                .Replace($"\"{replacement.Token}\"", replacement.Value, StringComparison.Ordinal)
                .Replace($"'{replacement.Token}'", replacement.Value, StringComparison.Ordinal)
                .Replace(replacement.Token, replacement.Value, StringComparison.Ordinal);
        }
        return command;
    }

    private static void AddToken(List<(string Token, string Value)> replacements, string token, string value)
    {
        token = token.Trim();
        if (token.Length > 0)
            replacements.Add((token, value));
    }

    private void LoadDefaultDefinitions()
    {
        var candidates = new[]
        {
            Path.Combine(DefineDirectory, "data.txt"),
            Path.Combine(AppContext.BaseDirectory, "defines", "data.txt"),
            Path.Combine(Environment.CurrentDirectory, "define", "data.txt"),
            Path.Combine(Environment.CurrentDirectory, "defines", "data.txt")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is not null) LoadDefinitionsFromFile(path);
    }

    private void LoadDefinitions_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "V2 tanım dosyasını seç",
            Filter = "Tanım dosyası (data.txt)|data.txt|Metin dosyaları (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*",
            FileName = "data.txt"
        };
        if (dialog.ShowDialog() == true)
        {
            var path = ImportDefineFile(dialog.FileName, "data.txt");
            LoadDefinitionsFromFile(path);
        }
    }

    private void LoadDefinitionsFromFile(string path)
    {
        try
        {
            DynamicDefinitionsPanel.Children.Clear();
            _definitionBindings.Clear();
            _deviceTypeCombo = null;
            var supportedNames = new HashSet<string>(
                Enumerable.Range(1, 8).Select(index => $"val{index}_lb").Append("devices_lb"),
                StringComparer.OrdinalIgnoreCase);

            foreach (var line in File.ReadAllLines(path))
            {
                var parts = line.Split(new[] { '=', '|' }, StringSplitOptions.None);
                if (parts.Length != 4 || !supportedNames.Contains(parts[0].Trim())) continue;

                var token = parts[1].Trim();
                if (token.Length == 0) continue;
                var options = parts[2].Split('>').Select(value => value.Trim()).Where(value => value.Length > 0).ToArray();

                DynamicDefinitionsPanel.Children.Add(new TextBlock
                {
                    Text = token,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 7, 0, 3),
                    ToolTip = $"Komut dosyasında kullanılan yer tutucu: {token}"
                });

                if (options.Length > 0)
                {
                    var comboBox = new ComboBox
                    {
                        Style = (Style)FindResource("Field"),
                        Margin = new Thickness(0, 0, 0, 4),
                        ItemsSource = options
                    };
                    if (int.TryParse(parts[3].Trim(), out var selectedIndex) && selectedIndex >= 0 && selectedIndex < options.Length)
                        comboBox.SelectedIndex = selectedIndex;
                    else
                        comboBox.SelectedIndex = 0;
                    DynamicDefinitionsPanel.Children.Add(comboBox);
                    _definitionBindings.Add((token, () => comboBox.SelectedItem?.ToString() ?? string.Empty));
                    if (string.Equals(parts[0].Trim(), "devices_lb", StringComparison.OrdinalIgnoreCase))
                    {
                        _deviceTypeCombo = comboBox;
                        comboBox.SelectionChanged += DeviceTypeCombo_SelectionChanged;
                    }
                }
                else
                {
                    var textBox = new TextBox { Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 0, 0, 4) };
                    DynamicDefinitionsPanel.Children.Add(textBox);
                    _definitionBindings.Add((token, () => textBox.Text));
                }
            }

            DefinitionsFileStatus.Text = $"Yüklendi: {path}";
            if (_definitionBindings.Count == 0)
                DefinitionsFileStatus.Text += " · val1_lb–val8_lb veya devices_lb tanımı bulunamadı.";
            RefreshCommandSuggestions();
        }
        catch (Exception ex)
        {
            DefinitionsFileStatus.Text = $"Tanım dosyası okunamadı: {ex.Message}";
            AppendLog($"Tanım dosyası okunamadı: {ex.Message}");
        }
    }

    private sealed class SystemEntry(string raw, string[] names, string[] ips)
    {
        public string Raw { get; } = raw;
        public string[] Names { get; } = names;
        public string[] Ips { get; } = ips;
        public override string ToString() => Raw;
    }

    private string CurrentDeviceType => _deviceTypeCombo?.SelectedItem?.ToString() ?? string.Empty;

    private void DeviceTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshCommandSuggestions();

    private void RefreshCommandSuggestions()
    {
        if (!IsInitialized || CommandSuggestionsList is null) return;

        var device = CurrentDeviceType;
        var suggestions = new List<CommandSuggestion>();
        try
        {
            if (Directory.Exists(ConfigDirectory))
            {
                foreach (var path in Directory.GetFiles(ConfigDirectory, "*.txt")
                             .Where(path => !string.Equals(Path.GetFileName(path), "filelist.txt", StringComparison.OrdinalIgnoreCase)))
                {
                    var lines = File.ReadAllLines(path);
                    for (var index = 3; index < lines.Length; index++)
                    {
                        var rawCommand = lines[index].Trim();
                        if (rawCommand.Length == 0 || rawCommand.StartsWith('#')) continue;
                        var command = rawCommand.Split(new[] { "//" }, 2, StringSplitOptions.None)[0].Trim();
                        if (command.Length == 0) continue;

                        suggestions.Add(new CommandSuggestion(
                            device,
                            rawCommand,
                            Path.GetFileName(path),
                            index + 1,
                            _commandAdvisor.Score(device, Path.GetFileNameWithoutExtension(path), command)));
                    }
                }
            }

            var ranked = suggestions
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.LineNumber)
                .Take(50)
                .ToArray();
            CommandSuggestionsList.ItemsSource = ranked;
            CommandSuggestionContext.Text = device.Length == 0
                ? "Cihaz türü seçilmedi · Genel komutlar listeleniyor"
                : $"Cihaz türü: {device} · {ranked.Length} öneri";
            CommandSuggestionStatus.Text = ranked.Length == 0
                ? "Öneri bulunamadı. config klasörüne komut TXT dosyaları ekleyin."
                : "Sıralama cihaz adı eşleşmesi ve verdiğiniz geri bildirimle zamanla iyileşir.";
        }
        catch (Exception ex)
        {
            CommandSuggestionsList.ItemsSource = null;
            CommandSuggestionContext.Text = "Öneriler yüklenemedi";
            CommandSuggestionStatus.Text = ex.Message;
        }
    }

    private void SendSuggestedCommand_Click(object sender, RoutedEventArgs e)
    {
        if (CommandSuggestionsList.SelectedItem is not CommandSuggestion suggestion)
        {
            AppendLog("Göndermek için bir komut önerisi seçin.");
            return;
        }

        try
        {
            SendCommandLine(suggestion.Command);
            RefreshCommandSuggestions();
            AppendLog($"Önerilen komut gönderildi: {suggestion.FileName}, satır {suggestion.LineNumber}");
        }
        catch (Exception ex) { AppendLog($"Önerilen komut gönderilemedi: {ex.Message}"); }
    }

    private void CommandSuggestionFeedback_Click(object sender, RoutedEventArgs e)
    {
        if (CommandSuggestionsList.SelectedItem is not CommandSuggestion suggestion)
        {
            AppendLog("Geri bildirim vermek için bir komut önerisi seçin.");
            return;
        }

        var helpful = Equals((sender as FrameworkElement)?.Tag, "helpful");
        _commandAdvisor.RecordFeedback(suggestion.Device, suggestion.Command, helpful);
        RefreshCommandSuggestions();
        CommandSuggestionStatus.Text = helpful
            ? "Geri bildirim kaydedildi; bu komut benzer cihazlar için öne çıkarılacak."
            : "Geri bildirim kaydedildi; bu komut benzer cihazlar için geriye alınacak.";
    }

    private void ResetCommandLearning_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "Kaydedilen komut gönderim geçmişi ve yararlı/yararsız geri bildirimler silinsin mi?",
            "Öneri öğrenme verisini sıfırla",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        _commandAdvisor.Reset();
        RefreshCommandSuggestions();
        CommandSuggestionStatus.Text = "Öğrenme verisi sıfırlandı. Öneriler başlangıç sıralamasına döndü.";
    }

    private void LoadDefaultSystemList()
    {
        var candidates = new[]
        {
            Path.Combine(DefineDirectory, "syslist.txt"),
            Path.Combine(AppContext.BaseDirectory, "defines", "syslist.txt"),
            Path.Combine(Environment.CurrentDirectory, "define", "syslist.txt"),
            Path.Combine(Environment.CurrentDirectory, "defines", "syslist.txt")
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is not null) LoadSystemList(path);
    }

    private void LoadSystemList_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "V2 sistem listesi seç",
            Filter = "Sistem listesi (syslist.txt)|syslist.txt|Metin dosyaları (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*",
            FileName = "syslist.txt"
        };
        if (dialog.ShowDialog() == true)
        {
            var path = ImportDefineFile(dialog.FileName, "syslist.txt");
            LoadSystemList(path);
        }
    }

    private string ImportDefineFile(string sourcePath, string fileName)
    {
        Directory.CreateDirectory(DefineDirectory);
        var destination = Path.Combine(DefineDirectory, fileName);
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            File.Copy(sourcePath, destination, overwrite: true);
        return destination;
    }

    private void LoadSystemList(string path)
    {
        try
        {
            _systemEntries.Clear();
            foreach (var line in File.ReadAllLines(path))
            {
                var names = ExtractDelimitedValues(line, '(', ')');
                var ips = ExtractDelimitedValues(line, '[', ']');
                if (names.Length > 0 || ips.Length > 0)
                    _systemEntries.Add(new SystemEntry(line, names, ips));
            }
            SystemSearchResults.ItemsSource = null;
            ApplySystemSearch();
            SystemListStatus.Text = $"Sistem listesi yüklendi: {path}";
        }
        catch (Exception ex)
        {
            SystemListStatus.Text = $"Sistem listesi okunamadı: {ex.Message}";
            AppendLog($"Sistem listesi okunamadı: {ex.Message}");
        }
    }

    private static string[] ExtractDelimitedValues(string source, char open, char close)
    {
        var start = source.IndexOf(open);
        var end = start >= 0 ? source.IndexOf(close, start + 1) : -1;
        return start >= 0 && end > start
            ? source[(start + 1)..end].Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
    }

    private void SystemSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySystemSearch();

    private void ApplySystemSearch()
    {
        var query = SystemSearchBox.Text.Trim();
        var results = string.IsNullOrEmpty(query)
            ? _systemEntries
            : _systemEntries.Where(entry => entry.Raw.Contains(query, StringComparison.OrdinalIgnoreCase)
                || entry.Names.Any(name => name.Contains(query, StringComparison.OrdinalIgnoreCase)));
        SystemSearchResults.ItemsSource = results.ToList();
    }

    private void SystemSearchResults_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SystemSearchResults.SelectedItem is not SystemEntry entry) return;
        _updatingSystemFields = true;
        try
        {
            SystemNameChoice.ItemsSource = entry.Names;
            SystemIpChoice.ItemsSource = entry.Ips;
            SystemNameChoice.SelectedIndex = entry.Names.Length > 0 ? 0 : -1;
            SystemIpChoice.SelectedIndex = entry.Ips.Length > 0 ? 0 : -1;
            if (_deviceTypeCombo is not null)
            {
                var vendor = entry.Raw.Contains("ALC", StringComparison.OrdinalIgnoreCase) ? "ALC"
                    : entry.Raw.Contains("HUA", StringComparison.OrdinalIgnoreCase) ? "HUA"
                    : entry.Raw.Contains("ZTE", StringComparison.OrdinalIgnoreCase) ? "ZTE"
                    : string.Empty;
                var match = _deviceTypeCombo.Items.Cast<object>().Select((item, index) => (item, index))
                    .FirstOrDefault(pair => vendor.Length > 0 && pair.item.ToString()?.Contains(vendor, StringComparison.OrdinalIgnoreCase) == true);
                if (match.item is not null) _deviceTypeCombo.SelectedIndex = match.index;
            }
        }
        finally { _updatingSystemFields = false; }

        if (SystemIpChoice.SelectedItem is string ip) SetSystemIp(ip);
    }

    private void SystemNameChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingSystemFields || SystemSearchResults.SelectedItem is not SystemEntry entry) return;
        var index = SystemNameChoice.SelectedIndex;
        if (index >= 0 && index < entry.Ips.Length)
        {
            _updatingSystemFields = true;
            SystemIpChoice.SelectedIndex = index;
            _updatingSystemFields = false;
            if (SystemIpChoice.SelectedItem is string ip) SetSystemIp(ip);
        }
    }

    private void SystemIpChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingSystemFields && SystemIpChoice.SelectedItem is string ip) SetSystemIp(ip);
    }

    private void SetSystemIp(string ip)
    {
        SystemIpValueBox.Text = ip;
        UpdateRouteIp();
    }

    private void SystemIpValueBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRouteIp();
    private void GatewayBlocksCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateRouteIp();

    private void UpdateRouteIp()
    {
        if (SystemIpValueBox is null || GatewayBlocksCombo?.SelectedItem is not ComboBoxItem selectedBlock) return;
        if (!int.TryParse(selectedBlock.Content?.ToString(), out var blockCount)) return;
        var ip = SystemIpValueBox.Text.Trim();
        if (ip.Length == 0)
        {
            RouteIpValueBox.Text = string.Empty;
            return;
        }

        var separator = ip.Contains('.') ? '.' : ':';
        var blocks = ip.Split(separator);
        if (blocks.Length >= blockCount)
            RouteIpValueBox.Text = string.Join(separator, blocks.Take(blockCount));
    }

    private void RefreshPorts()
    {
        var current = PortCombo.SelectedItem as string;
        var ports = SerialPort.GetPortNames().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        PortCombo.ItemsSource = ports;
        if (current is not null && ports.Contains(current))
            PortCombo.SelectedItem = current;
        else if (ports.Length > 0)
            PortCombo.SelectedIndex = 0;
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_serialPort?.IsOpen == true)
        {
            ClosePort();
            return;
        }

        if (PortCombo.SelectedItem is not string portName)
        {
            MessageBox.Show("Önce kullanılabilir bir seri port seçin.", "Port seçilmedi", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshPorts();
            return;
        }

        try
        {
            var baudRate = int.Parse(((ComboBoxItem)BaudCombo.SelectedItem!).Content.ToString()!);
            var dataBits = int.Parse(((ComboBoxItem)DataBitsCombo.SelectedItem!).Content.ToString()!);
            var stopBits = ((ComboBoxItem)StopBitsCombo.SelectedItem!).Content.ToString() == "2" ? StopBits.Two : StopBits.One;
            var parity = ((ComboBoxItem)ParityCombo.SelectedItem!).Content.ToString() switch
            {
                "Çift" => Parity.Even,
                "Tek" => Parity.Odd,
                _ => Parity.None
            };

            var serialPort = new SerialPort(portName, baudRate, parity, dataBits, stopBits)
            {
                Encoding = Encoding.UTF8,
                ReadTimeout = 500,
                WriteTimeout = 500,
                NewLine = "\r\n"
            };
            serialPort.DataReceived += SerialPort_DataReceived;
            serialPort.Open();
            _serialPort = serialPort;
            _receivedBytes = 0;
            ReceivedCount.Text = "0 B";
            ConnectButton.Content = "Bağlantıyı kes";
            ConnectButton.Background = new SolidColorBrush(Color.FromRgb(178, 72, 92));
            ConnectionStatus.Text = $"BAĞLI · {portName}";
            ConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(111, 224, 169));
            StatusDot.Fill = new SolidColorBrush(Color.FromRgb(69, 207, 143));
            SessionState.Text = "Bağlandı";
            AppendLog($"Bağlantı açıldı: {portName} · {baudRate} baud, {dataBits} bit");
        }
        catch (Exception ex)
        {
            ClosePort();
            MessageBox.Show($"Seri porta bağlanılamadı. Portun başka bir uygulama tarafından kullanılmadığını kontrol edin.\n\n{ex.Message}", "Bağlantı hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClosePort()
    {
        var port = _serialPort;
        _serialPort = null;
        if (port is not null)
        {
            port.DataReceived -= SerialPort_DataReceived;
            try { if (port.IsOpen) port.Close(); } catch (IOException) { }
            port.Dispose();
        }

        if (_closing) return;
        ConnectButton.Content = "Bağlan";
        ConnectButton.ClearValue(BackgroundProperty);
        ConnectionStatus.Text = "BAĞLANTI YOK";
        ConnectionStatus.Foreground = new SolidColorBrush(Color.FromRgb(170, 180, 200));
        StatusDot.Fill = new SolidColorBrush(Color.FromRgb(105, 117, 140));
        SessionState.Text = "Bekliyor";
    }

    private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort port) return;
        try
        {
            var count = port.BytesToRead;
            if (count <= 0) return;
            var buffer = new byte[count];
            var bytesRead = port.Read(buffer, 0, count);
            if (bytesRead <= 0) return;
            var data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            Dispatcher.BeginInvoke(() =>
            {
                if (_closing || !IsLoaded) return;
                _receivedBytes += bytesRead;
                ReceivedCount.Text = FormatBytes(_receivedBytes);
                AppendLog(data, isIncoming: true);
            });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_closing) AppendLog($"Okuma hatası: {ex.Message}");
            });
        }
    }

    private void LogBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            e.Handled = true;
            if (_serialPort?.IsOpen == true)
            {
                try { _serialPort.Write("\t"); }
                catch (Exception ex) { AppendLog($"Seri porta Tab gönderilemedi: {ex.Message}"); }
            }
            return;
        }

        if (e.Key == Key.Back)
        {
            e.Handled = true;
            if (_serialPort?.IsOpen == true)
            {
                try { _serialPort.Write("\b"); }
                catch (Exception ex) { AppendLog($"Seri porta Backspace gönderilemedi: {ex.Message}"); }
            }

            if (cliInputCursor > 0)
            {
                cliInputBuffer = cliInputBuffer.Remove(cliInputCursor - 1, 1);
                cliInputCursor--;
            }
            return;
        }

        if (e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            cliInputCursor = e.Key == Key.Left
                ? Math.Max(0, cliInputCursor - 1)
                : Math.Min(cliInputBuffer.Length, cliInputCursor + 1);
            return;
        }

        if (e.Key is Key.Up or Key.Down or Key.Delete)
            e.Handled = true;
    }

    private void LogBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = true;
        cliInputBuffer = cliInputBuffer.Insert(cliInputCursor, e.Text);
        cliInputCursor += e.Text.Length;
        if (_serialPort?.IsOpen != true) return;
        try { _serialPort.Write(e.Text); }
        catch (Exception ex) { AppendLog($"Seri porta veri gönderilemedi: {ex.Message}"); }
    }

    private void LogBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ResetCliInput();
        if (_serialPort?.IsOpen == true)
        {
            try
            {
                var lineEnding = EndLineCombo.SelectedValue?.ToString() switch
                {
                    "CRLF" => "\r\n",
                    "LF" => "\n",
                    "CR" => "\r",
                    _ => string.Empty
                };
                if (lineEnding.Length > 0) _serialPort.Write(lineEnding);
            }
            catch (Exception ex) { AppendLog($"Seri porta satır sonu gönderilemedi: {ex.Message}"); }
        }
        else
        {
            MessageBox.Show("Seri port bağlantısı kapalı.", "Bağlantı kapalı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ResetCliInput()
    {
        cliInputBuffer = string.Empty;
        cliInputCursor = 0;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        LogBox.Focus();
    }

    private void AppendLog(string message, bool isIncoming = false, bool isOutgoing = false)
    {
        string output;
        if (isIncoming)
        {
            DetectIncomingEndLine(message);
            var normalized = new StringBuilder(message.Length + 8);
            foreach (var character in message)
            {
                if (_pendingCarriageReturn)
                {
                    _pendingCarriageReturn = false;
                    if (character == '\n') continue;
                }

                if (character == '\r')
                {
                    normalized.Append(Environment.NewLine);
                    _pendingCarriageReturn = true;
                }
                else if (character == '\n')
                {
                    normalized.Append(Environment.NewLine);
                }
                else
                {
                    normalized.Append(character);
                }
            }

            output = normalized.ToString();
            if (output.Length == 0) return;
        }
        else
        {
            var prefix = TimestampCheck.IsChecked == true ? $"[{DateTime.Now:HH:mm:ss.fff}] " : string.Empty;
            output = prefix + message + Environment.NewLine;
        }

        LogBox.AppendText(output);
        if (AutoScrollCheck.IsChecked == true) LogBox.ScrollToEnd();
    }

    private void DetectIncomingEndLine(string message)
    {
        if (_endLineDetected) return;

        foreach (var character in message)
        {
            if (_pendingDetectedCarriageReturn)
            {
                _pendingDetectedCarriageReturn = false;
                _carriageReturnDetectionTimer.Stop();
                if (character == '\n')
                {
                    SetDetectedEndLine("CRLF");
                    return;
                }

                SetDetectedEndLine("CR");
                return;
            }

            if (character == '\r')
            {
                _pendingDetectedCarriageReturn = true;
                _carriageReturnDetectionTimer.Stop();
                _carriageReturnDetectionTimer.Start();
            }
            else if (character == '\n')
            {
                SetDetectedEndLine("LF");
                return;
            }
        }
    }

    private void SetDetectedEndLine(string value)
    {
        if (_endLineDetected) return;
        _endLineDetected = true;
        _pendingDetectedCarriageReturn = false;
        _carriageReturnDetectionTimer.Stop();
        EndLineCombo.SelectedValue = value;
        DetectedEndLineStatus.Text = $"Algılandı: {value}";
    }

    private static string FormatBytes(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024d:0.##} KB";
}
