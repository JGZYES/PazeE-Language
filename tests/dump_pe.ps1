param([string]$Path)
$bytes = [System.IO.File]::ReadAllBytes($Path)
$hex = ($bytes | ForEach-Object { '{0:X2}' -f $_ }) -join ' '
$e_lfanew = [BitConverter]::ToInt32($bytes, 0x3C)
Write-Host "DOS MZ: $([char]$bytes[0])$([char]$bytes[1])  e_lfanew=0x$($e_lfanew.ToString('X'))"
$pe = $e_lfanew
Write-Host "PE sig: $([char]$bytes[$pe])$([char]$bytes[$pe+1])"
$mach = [BitConverter]::ToUInt16($bytes, $pe+4)
$nsec = [BitConverter]::ToUInt16($bytes, $pe+6)
$optHdrSize = [BitConverter]::ToUInt16($bytes, $pe+20)
Write-Host "Machine=0x$($mach.ToString('X'))  Sections=$nsec  OptHdrSize=$optHdrSize"
$opt = $pe + 24
$magic = [BitConverter]::ToUInt16($bytes, $opt)
Write-Host "OptHdr Magic=0x$($magic.ToString('X')) (PE32+)"
$entry = [BitConverter]::ToUInt32($bytes, $opt+16)
$imageBase = [BitConverter]::ToInt64($bytes, $opt+24)
$sizeOfImage = [BitConverter]::ToUInt32($bytes, $opt+56)
$sizeOfHeaders = [BitConverter]::ToUInt32($bytes, $opt+60)
Write-Host "EntryPoint=0x$($entry.ToString('X'))  ImageBase=0x$($imageBase.ToString('X'))"
Write-Host "SizeOfImage=0x$($sizeOfImage.ToString('X'))  SizeOfHeaders=0x$($sizeOfHeaders.ToString('X'))"
# DataDirectory[1] = Import (at opt+112 for PE32+... actually opt+112 is data dir start, each 8 bytes; dir[1] at opt+120)
$ddBase = $opt + 112
$importRva = [BitConverter]::ToUInt32($bytes, $ddBase + 8)
$importSize = [BitConverter]::ToUInt32($bytes, $ddBase + 12)
Write-Host "Import Dir RVA=0x$($importRva.ToString('X'))  Size=$importSize"
# Sections
$secStart = $opt + $optHdrSize
Write-Host "--- Sections ---"
for ($i=0; $i -lt $nsec; $i++) {
    $s = $secStart + $i*40
    $name = [System.Text.Encoding]::ASCII.GetString($bytes, $s, 8).TrimEnd([char]0)
    $vsize = [BitConverter]::ToUInt32($bytes, $s+8)
    $vrva = [BitConverter]::ToUInt32($bytes, $s+12)
    $rawsize = [BitConverter]::ToUInt32($bytes, $s+16)
    $rawptr = [BitConverter]::ToUInt32($bytes, $s+20)
    $flags = [BitConverter]::ToUInt32($bytes, $s+36)
    Write-Host ("  {0,-8} VSize=0x{1:X} RVA=0x{2:X} RawSize=0x{3:X} RawPtr=0x{4:X} Flags=0x{5:X}" -f $name,$vsize,$vrva,$rawsize,$rawptr,$flags)
}
# Dump entry point bytes (first 32 bytes of .text)
$textRva = 0x1000
# find .text raw ptr
for ($i=0; $i -lt $nsec; $i++) {
    $s = $secStart + $i*40
    $name = [System.Text.Encoding]::ASCII.GetString($bytes, $s, 8).TrimEnd([char]0)
    if ($name -eq '.text') {
        $rawptr = [BitConverter]::ToUInt32($bytes, $s+20)
        Write-Host "--- .text first 64 bytes (raw ptr 0x$($rawptr.ToString('X'))) ---"
        $sb = New-Object System.Text.StringBuilder
        for ($k=0; $k -lt 64; $k++) { [void]$sb.Append(('{0:X2} ' -f $bytes[$rawptr+$k])) }
        Write-Host $sb.ToString()
    }
}
