$path = "samples\Wpf.Ui.servoStudio\Views\Pages\MotionPages\MotionTypePage.xaml"
$content = Get-Content $path -Raw

# Replace NumberBox and ComboBox widths
$content = $content -replace 'MinWidth="260" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"'
$content = $content -replace 'MinWidth="220" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"'

function AddUnit($pattern, $unit) {
    # .NET regex, (?s) makes . match newlines. But we can just use \s* explicitly between elements
    $regex = '(<TextBlock Text="[^"]*' + $pattern + '[^"]*".*?Style="\{StaticResource LabelStyle\}"\s*/\>)\s*(<ui:NumberBox[^>]*?/\>)\s*(<Slider[^>]*?Margin=")12,0,0,0("[^>]*?/\>)'
    $replacement = '$1' + "`r`n                            " + '$2' + "`r`n                            <TextBlock Text=`"$unit`" VerticalAlignment=`"Center`" Margin=`"12,0,0,0`" Width=`"42`" FontSize=`"14`" Foreground=`"{DynamicResource TextFillColorSecondaryBrush}`" />`r`n                            " + '$312,0,32,0$4'
    return [System.Text.RegularExpressions.Regex]::Replace($content, $regex, $replacement)
}

$content = AddUnit '0x607A' 'inc'
$content = AddUnit '0x6081' 'inc/s'
$content = AddUnit '0x6083' 'inc/s²'
$content = AddUnit '0x6084' 'inc/s²'
$content = AddUnit '0x6085' 'inc/s²'
$content = AddUnit '0x60FF' 'inc/s'
$content = AddUnit '0x6071' '‰'
$content = AddUnit '0x6072' '‰'
$content = AddUnit '0x6087' '‰/s'
$content = AddUnit '0x6099-1' 'inc/s'
$content = AddUnit '0x6099-2' 'inc/s'
$content = AddUnit '0x609A' 'inc/s²'
$content = AddUnit '0x60B0' 'inc'
$content = AddUnit '0x60B1' 'inc/s'
$content = AddUnit '0x60B2' '‰'
$content = AddUnit '0x60C2' 'µs'

Set-Content -Path $path -Value $content -Encoding UTF8
Write-Host "Done Replacement Phase 1"
