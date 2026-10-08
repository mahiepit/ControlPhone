# Build ControlPhone Keyboard (APK) bằng Android SDK + JDK của Android Studio, không cần Gradle.
# Kết quả: native/keyboard/ControlPhoneKeyboard.apk (được chép kèm ControlPhone.exe).
# Khoá ký nằm ở %USERPROFILE%\.controlphone\keyboard.jks (tự tạo lần đầu, KHÔNG đưa lên git).
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$sdk = if ($env:ANDROID_HOME) { $env:ANDROID_HOME } else { "$env:LOCALAPPDATA\Android\Sdk" }
$bt = Get-ChildItem "$sdk\build-tools" | Sort-Object { [version]($_.Name -replace '[^\d.]', '') } | Select-Object -Last 1 -ExpandProperty FullName
$plat = Get-ChildItem "$sdk\platforms" | Sort-Object { [double](($_.Name -replace 'android-', '') -replace '[^\d.]', '') } | Select-Object -Last 1 -ExpandProperty FullName
$jdk = if ($env:JAVA_HOME) { "$env:JAVA_HOME\bin" } else { 'C:\Program Files\Android\Android Studio\jbr\bin' }
$androidJar = "$plat\android.jar"
$out = Join-Path $here 'build'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force "$out\res", "$out\gen", "$out\classes", "$out\dex" | Out-Null
Write-Host "build-tools: $bt`nplatform: $plat"

# 1) tài nguyên + manifest
& "$bt\aapt2.exe" compile --dir "$here\res" -o "$out\res\res.zip"
& "$bt\aapt2.exe" link -I $androidJar --manifest "$here\AndroidManifest.xml" -o "$out\base.apk" --java "$out\gen" `
    --min-sdk-version 21 --target-sdk-version 34 "$out\res\res.zip"
if ($LASTEXITCODE) { throw 'aapt2 link lỗi' }

# 2) Java → class → dex
$srcs = Get-ChildItem "$here\src", "$out\gen" -Recurse -Filter *.java | ForEach-Object FullName
& "$jdk\javac.exe" -source 8 -target 8 -nowarn -Xlint:-options -cp $androidJar -d "$out\classes" @srcs
if ($LASTEXITCODE) { throw 'javac lỗi' }
$classes = Get-ChildItem "$out\classes" -Recurse -Filter *.class | ForEach-Object FullName
& "$bt\d8.bat" --release --min-api 21 --lib $androidJar --output "$out\dex" @classes
if ($LASTEXITCODE) { throw 'd8 lỗi' }

# 3) đóng gói + căn chỉnh + ký
Push-Location "$out\dex"; & "$jdk\jar.exe" uf "$out\base.apk" classes.dex; Pop-Location
& "$bt\zipalign.exe" -f -p 4 "$out\base.apk" "$out\aligned.apk"
$ks = "$env:USERPROFILE\.controlphone\keyboard.jks"
if (-not (Test-Path $ks)) {
    New-Item -ItemType Directory -Force (Split-Path $ks) | Out-Null
    # keytool in thông báo ra stderr → chạy qua cmd để PowerShell không coi là lỗi
    cmd /c "`"$jdk\keytool.exe`" -genkeypair -keystore `"$ks`" -storepass controlphone -keypass controlphone -alias cpkbd -keyalg RSA -keysize 2048 -validity 36500 -dname `"CN=ControlPhone Keyboard, O=ControlPhone`" 2>&1" | Out-Null
}
$apk = Join-Path $here 'ControlPhoneKeyboard.apk'
& "$bt\apksigner.bat" sign --ks $ks --ks-pass pass:controlphone --key-pass pass:controlphone --ks-key-alias cpkbd --out $apk "$out\aligned.apk"
if ($LASTEXITCODE) { throw 'apksigner lỗi' }
& "$bt\apksigner.bat" verify $apk
Remove-Item $out -Recurse -Force
Write-Host "OK: $apk ($([math]::Round((Get-Item $apk).Length / 1KB)) KB)"
