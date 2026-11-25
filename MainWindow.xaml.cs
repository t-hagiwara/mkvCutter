using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Win32;

namespace MkvCutter;

public partial class MainWindow : Window
{
    private static readonly Regex TimePattern = new(
        @"^(?<hours>\d{2,}):(?<minutes>[0-5]\d):(?<seconds>[0-5]\d)\.(?<milliseconds>\d{3})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private Process? _activeProcess;
    private string? _mkvmergePath;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _mkvmergePath = FindMkvMerge();
        if (_mkvmergePath is not null)
        {
            SetStatus($"mkvmerge を検出しました: {_mkvmergePath}");
            return;
        }

        _mkvmergePath = LoadSavedMkvMergePath();
        if (_mkvmergePath is not null)
        {
            SetStatus($"保存済みのmkvmergeを使用します: {_mkvmergePath}");
            AppendLog($"mkvmerge: {_mkvmergePath}");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "mkvmerge.exe を選択してください",
            Filter = "mkvmerge.exe|mkvmerge.exe",
            CheckFileExists = true,
            FileName = "mkvmerge.exe"
        };
        if (dialog.ShowDialog(this) == true)
        {
            _mkvmergePath = dialog.FileName;
            AppendLog($"mkvmerge: {_mkvmergePath}");
            if (TrySaveMkvMergePath(_mkvmergePath, out var error))
            {
                SetStatus("mkvmerge の設定を保存しました。");
            }
            else
            {
                SetStatus("mkvmerge は起動中のみ利用できます（設定保存に失敗）。");
                AppendLog($"設定保存エラー: {error}");
                MessageBox.Show(
                    this,
                    $"mkvmerge はこの起動中は利用できますが、設定を保存できませんでした。\n\n{GetSettingsFilePath()}\n{error}",
                    "設定を保存できません",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return;
        }

        MessageBox.Show(
            this,
            "mkvmerge.exe が必要です。次回起動時に指定してください。",
            "mkvmerge が未設定です",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        Close();
    }

    private string? LoadSavedMkvMergePath()
    {
        var settingsPath = GetSettingsFilePath();
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(settingsPath));
            if (settings is not null &&
                settings.TryGetValue("mkvmergePath", out var savedPath) &&
                !string.IsNullOrWhiteSpace(savedPath))
            {
                if (File.Exists(savedPath))
                {
                    return savedPath;
                }

                AppendLog($"保存済みのmkvmergeが見つかりません: {savedPath}");
            }
            else
            {
                AppendLog("設定ファイルにmkvmergeのパスがありません。");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            MessageBox.Show(
                this,
                $"保存済み設定を読み込めませんでした。mkvmerge.exe を選択してください。\n\n{settingsPath}\n{ex.Message}",
                "設定を読み込めません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return null;
    }

    private static bool TrySaveMkvMergePath(string path, out string? error)
    {
        try
        {
            var settings = new Dictionary<string, string>
            {
                ["mkvmergePath"] = path
            };
            File.WriteAllText(
                GetSettingsFilePath(),
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string GetSettingsFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "mkvCutter.settings.json");
    }

    private void BrowseInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "入力動画を選択",
            Filter = "動画・メディアファイル|*.mkv;*.mka;*.mks;*.mk3d;*.webm;*.mp4;*.mov;*.avi;*.mpg;*.mpeg;*.ts;*.m2ts;*.mp3;*.flac;*.wav|すべてのファイル|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetInputFile(dialog.FileName);
    }

    private void Input_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files &&
                    File.Exists(files[0])
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Input_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files ||
            !File.Exists(files[0]))
        {
            MessageBox.Show(
                this,
                "入力動画ファイルを1つだけドロップしてください。",
                "ファイルを設定できません",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        SetInputFile(files[0]);
    }

    private void SetInputFile(string filePath)
    {
        InputPathTextBox.Text = filePath;
        OutputPathTextBox.Text = Path.Combine(
            Path.GetDirectoryName(filePath)!,
            $"{Path.GetFileNameWithoutExtension(filePath)}_cut.mkv");
        StartButton.IsEnabled = File.Exists(filePath);
        SetStatus("入力動画を設定しました。");
    }

    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "切り出した動画の保存先",
            Filter = "Matroska 動画 (*.mkv)|*.mkv",
            DefaultExt = ".mkv",
            AddExtension = true,
            OverwritePrompt = false,
            FileName = Path.GetFileName(OutputPathTextBox.Text),
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(OutputPathTextBox.Text))
                ? Path.GetDirectoryName(OutputPathTextBox.Text)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog(this) == true)
        {
            OutputPathTextBox.Text = dialog.FileName;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (!TryValidateInputs(out var inputPath, out var outputPath, out var start, out var end))
        {
            return;
        }

        List<string> existingOutputFiles;
        try
        {
            existingOutputFiles = GetExistingOutputFiles(outputPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowValidationError($"保存先フォルダーを確認できませんでした。\n{ex.Message}");
            return;
        }

        if (existingOutputFiles.Count > 0)
        {
            var answer = MessageBox.Show(
                this,
                $"出力先に同じ名前または連番のファイルがすでにあります。上書きしますか？\n\n{outputPath}",
                "上書きの確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                return;
            }
        }

        var mkvmergePath = _mkvmergePath;
        if (string.IsNullOrWhiteSpace(mkvmergePath) || !File.Exists(mkvmergePath))
        {
            ShowValidationError("mkvmerge.exe が見つかりません。アプリを再起動して、実行ファイルを指定してください。");
            return;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = mkvmergePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--split");
        startInfo.ArgumentList.Add($"parts:{start}-{end}");
        startInfo.ArgumentList.Add(inputPath);

        var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("mkvmerge を起動できませんでした。");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            AppendLog($"起動エラー: {ex.Message}");
            SetStatus("mkvmerge を起動できませんでした。");
            return;
        }

        _activeProcess = process;
        _isCancelling = false;
        SetRunning(true);
        SetStatus("切り出し中...");
        AppendLog($"mkvmerge: {mkvmergePath}");
        AppendLog($"入力: {inputPath}");
        AppendLog($"範囲: {start} - {end}");
        AppendLog($"出力: {outputPath}");

        var standardOutputTask = ReadOutputAsync(process.StandardOutput);
        var standardErrorTask = ReadOutputAsync(process.StandardError);

        try
        {
            await process.WaitForExitAsync();
            await Task.WhenAll(standardOutputTask, standardErrorTask);

            if (_isCancelling)
            {
                AppendLog("処理をキャンセルしました。");
                SetStatus("キャンセルしました。");
            }
            else if (process.ExitCode == 0)
            {
                AppendLog("切り出しが完了しました。");
                SetStatus("完了しました。");
            }
            else
            {
                AppendLog($"mkvmerge が終了コード {process.ExitCode} で終了しました。");
                SetStatus($"エラー: 終了コード {process.ExitCode}");
            }
        }
        catch (Exception ex)
        {
            AppendLog($"実行エラー: {ex.Message}");
            SetStatus("実行中にエラーが発生しました。");
        }
        finally
        {
            _activeProcess = null;
            process.Dispose();
            SetRunning(false);
        }
    }

    private bool _isCancelling;

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_activeProcess is null)
        {
            return;
        }

        try
        {
            _isCancelling = true;
            _activeProcess.Kill(entireProcessTree: true);
            SetStatus("キャンセル中...");
            await _activeProcess.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (Win32Exception ex)
        {
            AppendLog($"キャンセルエラー: {ex.Message}");
            SetStatus("プロセスをキャンセルできませんでした。");
        }
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _activeProcess is null)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            "切り出し処理を終了してアプリを閉じますか？",
            "処理中です",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        e.Cancel = true;
        await StopProcessForCloseAsync();
        _allowClose = true;
        Close();
    }

    private async Task StopProcessForCloseAsync()
    {
        if (_activeProcess is null)
        {
            return;
        }

        try
        {
            _isCancelling = true;
            _activeProcess.Kill(entireProcessTree: true);
            await _activeProcess.WaitForExitAsync();
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
        catch (Win32Exception ex)
        {
            AppendLog($"終了エラー: {ex.Message}");
        }
    }

    private bool TryValidateInputs(
        out string inputPath,
        out string outputPath,
        out string start,
        out string end)
    {
        inputPath = InputPathTextBox.Text.Trim();
        outputPath = OutputPathTextBox.Text.Trim();
        start = StartTimeTextBox.Text.Trim();
        end = EndTimeTextBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
        {
            ShowValidationError("入力動画を選択してください。");
            return false;
        }

        if (!TryParseTime(start, out var startTime) || !TryParseTime(end, out var endTime))
        {
            ShowValidationError("時刻は「時:分:秒.ミリ秒」の形式で入力してください。\n例: 00:00:06.500");
            return false;
        }

        if (startTime >= endTime)
        {
            ShowValidationError("終了時刻は開始時刻より後にしてください。");
            return false;
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            ShowValidationError("保存先を指定してください。");
            return false;
        }

        if (!string.Equals(Path.GetExtension(outputPath), ".mkv", StringComparison.OrdinalIgnoreCase))
        {
            ShowValidationError("出力ファイルの拡張子は .mkv にしてください。");
            return false;
        }

        string fullInputPath;
        string fullOutputPath;
        try
        {
            fullInputPath = Path.GetFullPath(inputPath);
            fullOutputPath = Path.GetFullPath(outputPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowValidationError("入力または保存先のパスが正しくありません。");
            return false;
        }

        var outputDirectory = Path.GetDirectoryName(fullOutputPath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            ShowValidationError("保存先フォルダーが存在しません。");
            return false;
        }

        if (string.Equals(fullInputPath, fullOutputPath, StringComparison.OrdinalIgnoreCase))
        {
            ShowValidationError("入力動画と同じファイルには保存できません。");
            return false;
        }

        return true;
    }

    private static List<string> GetExistingOutputFiles(string outputPath)
    {
        var existingFiles = new List<string>();
        if (File.Exists(outputPath))
        {
            existingFiles.Add(outputPath);
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return existingFiles;
        }

        var outputName = Path.GetFileNameWithoutExtension(outputPath);
        var extension = Path.GetExtension(outputPath);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var fileName = Path.GetFileName(file);
            var fileExtension = Path.GetExtension(fileName);
            var fileStem = Path.GetFileNameWithoutExtension(fileName);
            var prefix = $"{outputName}-";
            if (!fileStem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(fileExtension, extension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var segmentNumber = fileStem[prefix.Length..];
            if (segmentNumber.Length >= 3 && segmentNumber.All(char.IsDigit))
            {
                existingFiles.Add(file);
            }
        }

        return existingFiles;
    }

    private static bool TryParseTime(string value, out TimeSpan time)
    {
        time = default;
        var match = TimePattern.Match(value);
        if (!match.Success ||
            !long.TryParse(match.Groups["hours"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hours))
        {
            return false;
        }

        var minutes = int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture);
        var milliseconds = int.Parse(match.Groups["milliseconds"].Value, CultureInfo.InvariantCulture);

        try
        {
            var totalMilliseconds = checked(
                checked(hours * 3_600_000L) +
                minutes * 60_000L +
                seconds * 1_000L +
                milliseconds);
            time = TimeSpan.FromTicks(checked(totalMilliseconds * TimeSpan.TicksPerMillisecond));
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static string? FindMkvMerge()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "mkvmerge.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries and continue searching.
            }
        }

        return null;
    }

    private async Task ReadOutputAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            AppendLog(line);
        }
    }

    private void ShowValidationError(string message)
    {
        SetStatus("入力を確認してください。");
        MessageBox.Show(this, message, "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SetRunning(bool isRunning)
    {
        StartButton.IsEnabled = !isRunning && File.Exists(InputPathTextBox.Text);
        CancelButton.IsEnabled = isRunning;
        InputPathTextBox.IsEnabled = !isRunning;
        StartTimeTextBox.IsEnabled = !isRunning;
        EndTimeTextBox.IsEnabled = !isRunning;
        OutputPathTextBox.IsEnabled = !isRunning;
    }

    private void SetStatus(string status)
    {
        StatusTextBlock.Text = status;
    }

    private void AppendLog(string message)
    {
        LogTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        LogTextBox.ScrollToEnd();
    }
}
