using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using UsageNotch.Services.Phone;
using RadioButton = System.Windows.Controls.RadioButton;

namespace UsageNotch;

/// <summary>Settings → Phone. Actions apply immediately (like Updates); they are not part of Save/Cancel.</summary>
public partial class SettingsWindow
{
    private PhoneLinkService? _phone;
    private IReadOnlyList<LinkAddress> _phoneAddresses = [];

    private void InitPhone()
    {
        _phone = (System.Windows.Application.Current as App)?.Phone;
        if (_phone is null) { PhoneTab.IsEnabled = false; return; }
        _phone.Changed += PhoneChanged;
        Closed += (_, _) => _phone.Changed -= PhoneChanged;
        RenderPhoneAddresses();
        PhoneChanged();
    }

    public void ShowPhoneTab() => PhoneTab.IsSelected = true;

    private void PhoneChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(PhoneChanged); return; }
        if (_phone is null) return;
        PhoneShareBox.IsChecked = _phone.Running;
        PhoneStatus.Text = _phone.Status;
        PhoneSwitchButton.Visibility = !_phone.Running && PhoneLinkService.LegacyLinkRunning() ? Visibility.Visible : Visibility.Collapsed;
        var sync = _phone.SyncEnabled;
        PhoneSyncSetup.Visibility = sync ? Visibility.Collapsed : Visibility.Visible;
        PhoneSyncOffButton.Visibility = sync ? Visibility.Visible : Visibility.Collapsed;
        PhoneSyncStatus.Text = _phone.SyncStatus.Length > 0 ? _phone.SyncStatus : sync ? "Internet sync is on." : "";
        var address = _phone.Address(_phoneAddresses);
        var canPair = _phone.Identity != null && address != null && (_phone.Running || sync);
        PhoneCopyButton.IsEnabled = PhoneExportButton.IsEnabled = canPair;
        if (canPair)
        {
            try { PhoneQr.Source = PairingQr.Render(_phone.Identity!.PairingUrl(address!.Address)); PhoneQrPlaceholder.Visibility = Visibility.Collapsed; }
            catch { PhoneQr.Source = null; PhoneQrPlaceholder.Text = "Couldn't draw the pairing code. Use Copy pairing code instead."; PhoneQrPlaceholder.Visibility = Visibility.Visible; }
        }
        else
        {
            PhoneQr.Source = null;
            PhoneQrPlaceholder.Text = address == null ? "No Wi-Fi or private network found. Connect this PC to the same Wi-Fi as your phone." : "Turn on sharing to show your pairing code";
            PhoneQrPlaceholder.Visibility = Visibility.Visible;
        }
        foreach (var child in PhoneAddresses.Children.OfType<RadioButton>()) child.IsChecked = (child.Tag as LinkAddress)?.Address == address?.Address;
    }

    private void RenderPhoneAddresses()
    {
        _phoneAddresses = PhoneIdentity.Addresses();
        PhoneAddresses.Children.Clear();
        foreach (var candidate in _phoneAddresses)
        {
            var option = new RadioButton { Content = candidate.ToString(), Tag = candidate, GroupName = "PhoneAddress", Style = (Style)FindResource("Segment"), Margin = new Thickness(0, 0, 8, 8) };
            option.Checked += (_, _) => { if (_phone != null && _phone.PreferredAddress != candidate.Address) _phone.PreferredAddress = candidate.Address; };
            PhoneAddresses.Children.Add(option);
        }
    }

    private async void PhoneShare_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is null) return;
        PhoneShareBox.IsEnabled = false;
        try { if (PhoneShareBox.IsChecked == true) await _phone.StartAsync(); else await _phone.StopAsync(); }
        finally { PhoneShareBox.IsEnabled = true; PhoneChanged(); }
    }

    private async void PhoneSwitch_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is null) return;
        PhoneSwitchButton.IsEnabled = false;
        try { await _phone.SwitchFromLegacyLinkAsync(); }
        finally { PhoneSwitchButton.IsEnabled = true; PhoneChanged(); }
    }

    private void PhoneCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_phone?.Identity is not { } identity || _phone.Address(_phoneAddresses) is not { } address) return;
        try { System.Windows.Clipboard.SetText(identity.PairingCode(address.Address)); PhoneStatus.Text = "Pairing code copied. Send it to yourself privately, then choose Paste pairing code on your phone."; }
        catch { PhoneStatus.Text = "Couldn't use the clipboard. Try again, or save a pairing file instead."; }
    }

    private void PhoneExport_Click(object sender, RoutedEventArgs e)
    {
        if (_phone?.Identity is not { } identity || _phone.Address(_phoneAddresses) is not { } address) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "UsageNotch-PC.usagenotch", Filter = "UsageNotch pairing|*.usagenotch", DefaultExt = ".usagenotch" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllText(dialog.FileName, identity.Pairing(address.Address)); PhoneStatus.Text = "Pairing file saved. Open it on your phone with Import PC pairing, then delete it."; }
        catch { PhoneStatus.Text = "Couldn't save the file. Choose another location."; }
    }

    private void PhoneDownload_Click(object sender, RoutedEventArgs e) => OpenLink("https://arnav-dugad.github.io/UsageNotch-Windows/#download");
    private void PhoneTokenHelp_Click(object sender, RoutedEventArgs e) => OpenLink("https://github.com/settings/personal-access-tokens/new");
    private static void OpenLink(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    private async void PhoneSyncOn_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is null) return;
        var token = PhoneToken.Password;
        if (string.IsNullOrWhiteSpace(token)) { PhoneSyncStatus.Text = "Paste a GitHub token first."; return; }
        PhoneSyncOnButton.IsEnabled = false; PhoneSyncStatus.Text = "Creating your secret sync gist…";
        try { await _phone.EnableSyncAsync(token); PhoneToken.Clear(); }
        catch (RelayRejectedException ex) { PhoneSyncStatus.Text = ex.Message; }
        catch { PhoneSyncStatus.Text = "Couldn't reach GitHub. Check your internet connection and try again."; }
        finally { PhoneSyncOnButton.IsEnabled = true; PhoneChanged(); }
    }

    private async void PhoneSyncOff_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is null) return;
        if (System.Windows.MessageBox.Show(this, "Turn off internet sync and delete the encrypted gist from your GitHub account? Phones keep working on the same Wi-Fi.", "Internet sync", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        PhoneSyncOffButton.IsEnabled = false;
        try { await _phone.DisableSyncAsync(); } finally { PhoneSyncOffButton.IsEnabled = true; PhoneChanged(); }
    }

    private async void PhoneRevoke_Click(object sender, RoutedEventArgs e)
    {
        if (_phone is null) return;
        if (System.Windows.MessageBox.Show(this, "Disconnect every paired phone? Existing pairing codes and files stop working, including their internet sync key.", "Revoke paired phones", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { await _phone.RevokeAsync(); } catch { PhoneStatus.Text = "Couldn't save the new key. Try again."; }
        PhoneChanged();
    }
}
