# Build ControlPhone recovery helper (cprec) bằng Android NDK: binary tĩnh, chạy được trong recovery (TWRP...).
# Kết quả: native/recovery/cprec-arm64, cprec-arm (được chép kèm ControlPhone.exe trong thư mục recovery\).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "$env:LOCALAPPDATA\Android\Sdk" }
$ndk = if ($env:ANDROID_NDK_HOME) { $env:ANDROID_NDK_HOME } else { Get-ChildItem "$sdk\ndk" -Directory | Sort-Object { [version]$_.Name } | Select-Object -Last 1 -ExpandProperty FullName }
$bin = "$ndk\toolchains\llvm\prebuilt\windows-x86_64\bin"
Write-Host "NDK: $ndk"
foreach ($t in @(@('aarch64-linux-android21', 'cprec-arm64'), @('armv7a-linux-androideabi21', 'cprec-arm'))) {
    & "$bin\clang.exe" --target=$($t[0]) -static -O2 -Wall -Wextra -o "$here\$($t[1])" "$here\cprec.c"
    if ($LASTEXITCODE) { throw "clang lỗi ($($t[0]))" }
    & "$bin\llvm-strip.exe" "$here\$($t[1])"
    Write-Host "$($t[1]): $((Get-Item "$here\$($t[1])").Length) byte"
}
