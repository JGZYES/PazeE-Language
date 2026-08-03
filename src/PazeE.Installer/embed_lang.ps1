# embed_lang.ps1 - Embed language transforms into MSI to create a multi-language MSI
# Usage: powershell -File embed_lang.ps1 -MsiPath <zh-cn.msi> -EnMst <en-us.mst> -TwMst <zh-tw.mst>

param(
    [Parameter(Mandatory=$true)][string]$MsiPath,
    [Parameter(Mandatory=$true)][string]$EnMst,
    [Parameter(Mandatory=$true)][string]$TwMst
)

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class MsiEmbed {
    const int MSIDBOPEN_TRANSACT = 1;
    const int MSIDBOPEN_READONLY = 0;
    const uint PID_TEMPLATE = 7;        // SummaryInformation: Platform;Language
    const int VT_LPSTR = 30;

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiOpenDatabaseW(string szDatabasePath, int szPersist, out IntPtr phDatabase);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiDatabaseOpenViewW(IntPtr hDatabase, string szQuery, out IntPtr phView);

    [DllImport("msi.dll")]
    static extern uint MsiViewExecute(IntPtr hView, IntPtr hRecord);

    [DllImport("msi.dll")]
    static extern uint MsiViewClose(IntPtr hView);

    [DllImport("msi.dll")]
    static extern uint MsiDatabaseCommit(IntPtr hDatabase);

    [DllImport("msi.dll")]
    static extern IntPtr MsiCreateRecord(uint cParams);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiRecordSetStringW(IntPtr hRecord, uint iField, string szValue);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiRecordSetStreamW(IntPtr hRecord, uint iField, string szFilePath);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiGetSummaryInformationW(IntPtr hDatabase, string szDatabasePath, uint uiUpdateCount, out IntPtr phSummaryInfo);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    static extern uint MsiSummaryInfoSetPropertyW(IntPtr hSummaryInfo, uint uiProperty, int uiType, int iValue, IntPtr pftValue, string szValue);

    [DllImport("msi.dll")]
    static extern uint MsiSummaryInfoPersist(IntPtr hSummaryInfo);

    static string Check(uint r, string ctx) {
        if (r != 0) throw new Exception("MSI error " + r + " at: " + ctx);
        return "OK";
    }

    public static void Embed(string msiPath, string enMst, string twMst) {
        IntPtr hDb;
        Check(MsiOpenDatabaseW(msiPath, MSIDBOPEN_TRANSACT, out hDb), "OpenDatabase");

        // Ensure _Storages table exists; create if missing
        try {
            IntPtr hView0;
            MsiDatabaseOpenViewW(hDb, "SELECT * FROM `_Storages`", out hView0);
            MsiViewExecute(hView0, IntPtr.Zero);
            MsiViewClose(hView0);
        } catch {
            Console.WriteLine("Creating _Storages table...");
            IntPtr hCV;
            Check(MsiDatabaseOpenViewW(hDb, "CREATE TABLE `_Storages` (`Name` CHAR(255) NOT NULL LOCALIZABLE, `Data` OBJECT NOT NULL PRIMARY KEY `Name`)", out hCV), "OpenView CREATE");
            Check(MsiViewExecute(hCV, IntPtr.Zero), "Execute CREATE");
            MsiViewClose(hCV);
        }

        EmbedStorage(hDb, "1033", enMst, "en-us");
        EmbedStorage(hDb, "1028", twMst, "zh-tw");

        // Update SummaryInformation Template (PID 7 = "Platform;Language") 为多语言
        // Windows Installer 运行时根据系统语言自动应用对应 transform；不匹配时显示语言选择
        IntPtr hSum;
        Check(MsiGetSummaryInformationW(hDb, null, 1, out hSum), "GetSummaryInfo");
        Check(MsiSummaryInfoSetPropertyW(hSum, PID_TEMPLATE, VT_LPSTR, 0, IntPtr.Zero, "x64;2052,1033,1028"), "SummarySetProperty");
        Check(MsiSummaryInfoPersist(hSum), "SummaryPersist");

        Check(MsiDatabaseCommit(hDb), "Commit");
        Console.WriteLine("Multi-language MSI built: " + msiPath);
    }

    static void EmbedStorage(IntPtr hDb, string storageName, string mstPath, string label) {
        Console.WriteLine("Embedding " + label + " transform as storage '" + storageName + "'...");
        IntPtr hView;
        Check(MsiDatabaseOpenViewW(hDb, "INSERT INTO `_Storages` (`Name`, `Data`) VALUES (?, ?)", out hView), "OpenView INSERT " + label);
        IntPtr hRec = MsiCreateRecord(2);
        Check(MsiRecordSetStringW(hRec, 1, storageName), "SetString " + label);
        Check(MsiRecordSetStreamW(hRec, 2, mstPath), "SetStream " + label);
        Check(MsiViewExecute(hView, hRec), "Execute INSERT " + label);
        MsiViewClose(hView);
    }
}
"@

[MsiEmbed]::Embed($MsiPath, $EnMst, $TwMst)
