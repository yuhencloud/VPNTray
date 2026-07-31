using System.Windows;
using VPNTray.Core;

namespace VPNTray.UI;

/// <summary>
/// Shared "import TOTP secret from a QR code" flow used by both the onboarding
/// wizard and the settings window. Decoding happens entirely on the local machine.
/// Returns the Base32 secret on success, or null (having shown the user a message) on failure.
/// </summary>
public static class QrImport
{
    public static string? FromImageFile(Window owner)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select QR code image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*",
        };
        if (dlg.ShowDialog(owner) != true) return null;

        try
        {
            return Interpret(owner, QrCodeReader.DecodeFile(dlg.FileName));
        }
        catch (Exception ex)
        {
            Warn(owner, $"Could not read the image: {ex.Message}");
            return null;
        }
    }

    public static async Task<string?> FromScreen(Window owner)
    {
        // Hide the window so it does not cover the QR code, let the screen repaint,
        // then capture all monitors and look for a QR code.
        owner.Visibility = Visibility.Hidden;
        try
        {
            await Task.Delay(300);
            var text = await Task.Run(QrCodeReader.DecodeScreen);
            return Interpret(owner, text);
        }
        catch (Exception ex)
        {
            Warn(owner, $"Screen capture failed: {ex.Message}");
            return null;
        }
        finally
        {
            owner.Visibility = Visibility.Visible;
            owner.Activate();
        }
    }

    private static string? Interpret(Window owner, string? decodedText)
    {
        if (decodedText is null)
        {
            Warn(owner, "No QR code found. Make sure the code is fully visible and try again.");
            return null;
        }

        var secret = OtpAuthParser.TryExtractSecret(decodedText);
        if (secret is null)
        {
            Warn(owner, "A QR code was found, but it does not contain a TOTP secret (expected an otpauth:// code).");
            return null;
        }
        return secret;
    }

    private static void Warn(Window owner, string message) =>
        MessageBox.Show(owner, message, "QR import", MessageBoxButton.OK, MessageBoxImage.Warning);
}
