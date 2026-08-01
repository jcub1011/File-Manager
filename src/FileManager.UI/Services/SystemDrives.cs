using FileManager.Contracts.Settings;
using System;
using System.Collections.Generic;
using System.IO;

namespace FileManager.UI.Services;

/// <summary>Lists the machine's mounted volumes for the settings drive-override picker.
///
/// Enumerated locally rather than fetched over IPC, for the same reason the folder pickers are
/// (architecture §2.3): the GUI runs as the same user on the same machine, so it sees the same drives
/// the engine will. The keys still go through the shared <see cref="VolumeKeys.Normalize"/> so a picked
/// drive matches what the engine derives from a scanned path.</summary>
public sealed class SystemDrives : ISystemDrives
{
    public IReadOnlyList<DriveOption> List()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex)
        {
            // An empty list degrades the picker to "type it yourself", which is still a working editor.
            Serilog.Log.Warning(ex, "Could not enumerate drives for the settings drive picker");
            return [];
        }

        List<DriveOption> options = new(drives.Length);
        foreach (DriveInfo drive in drives)
        {
            try
            {
                if (!drive.IsReady)
                    continue;                       // an empty optical bay or a dropped network mount
                string key = VolumeKeys.Normalize(drive.Name);
                if (key.Length == 0)
                    continue;
                options.Add(new DriveOption(key, Describe(drive, key)));
            }
            catch (Exception ex)
            {
                // Per-drive, so one unreachable volume costs its own row and nothing else — the same
                // shape as FileSystemService.EnumerateRoots.
                Serilog.Log.Warning(ex, "Skipping drive {Drive} in the settings drive picker", drive.Name);
            }
        }
        return options;
    }

    /// <summary>"C: — Windows (Fixed)" style, falling back to just the key and class when the volume
    /// reports no label. The bare <see cref="DriveClass"/> name rather than its longer <c>[Tooltip]</c>
    /// title ("Fixed (HDD/SSD)"): the picker is a narrow combo in a row of five controls, and the letter
    /// plus the volume label are what identify the drive — the class is only context.</summary>
    private static string Describe(DriveInfo drive, string key)
    {
        string label = "";
        try
        {
            label = drive.VolumeLabel ?? "";
        }
        catch (Exception ex)
        {
            // VolumeLabel can throw on volumes that answer IsReady — not worth losing the row over.
            Serilog.Log.Debug(ex, "No volume label available for {Drive}", drive.Name);
        }

        string upperKey = key.ToUpperInvariant();
        string className = Classify(drive.DriveType).ToString();
        return label.Length == 0 ? $"{upperKey} ({className})" : $"{upperKey} — {label} ({className})";
    }

    /// <summary>Mirrors <c>WindowsVolumeInfoProvider.GetDriveClass</c>'s mapping, expressed over
    /// <see cref="DriveType"/> because that is what <see cref="DriveInfo"/> reports. Kept in step with it
    /// so a picked drive and its drive-type override describe the same medium.</summary>
    private static DriveClass Classify(DriveType type) => type switch
    {
        DriveType.Removable => DriveClass.Removable,
        DriveType.Fixed => DriveClass.Fixed,
        DriveType.Network => DriveClass.Network,
        DriveType.CDRom => DriveClass.Optical,
        DriveType.Ram => DriveClass.Ram,
        _ => DriveClass.Unknown,
    };
}
