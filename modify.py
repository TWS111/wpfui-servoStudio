import re

path = r"samples\Wpf.Ui.servoStudio\Views\Pages\MotionPages\MotionTypePage.xaml"
with open(path, 'r', encoding='utf-8') as f:
    content = f.read()

content = content.replace('MinWidth="260" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"')
content = content.replace('MinWidth="220" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"')

def add_unit(c, pattern, unit):
    p = re.escape(pattern)
    # capture 1: Label
    # capture 2: NumberBox
    # capture 3: Slider Start
    # capture 4: Slider End
    regexStr = r'(<TextBlock\s+Text="[^"]*' + p + r'[^"]*".*?Style="\{StaticResource\s+LabelStyle\}"\s*/>)\s*(<ui:NumberBox[^>]*?/>)\s*(<Slider[^>]*?Margin=")12,0,0,0("[^>]*?/>)'
    
    rep = r'\1\n                            \2\n                            <TextBlock Text="' + unit + r'" VerticalAlignment="Center" Margin="12,0,0,0" Width="45" FontSize="15" Foreground="{DynamicResource TextFillColorSecondaryBrush}" />\n                            \312,0,32,0\4'
    return re.sub(regexStr, rep, c, flags=re.DOTALL)

content = add_unit(content, "0x607A", "inc")
content = add_unit(content, "0x6081", "inc/s")
content = add_unit(content, "0x6083", "inc/s²")
content = add_unit(content, "0x6084", "inc/s²")
content = add_unit(content, "0x6085", "inc/s²")
content = add_unit(content, "0x60FF", "inc/s")
content = add_unit(content, "0x6071", "‰")
content = add_unit(content, "0x6072", "‰")
content = add_unit(content, "0x6087", "‰/s")
content = add_unit(content, "0x6099-1", "inc/s")
content = add_unit(content, "0x6099-2", "inc/s")
content = add_unit(content, "0x609A", "inc/s²")
content = add_unit(content, "0x60B0", "inc")
content = add_unit(content, "0x60B1", "inc/s")
content = add_unit(content, "0x60B2", "‰")
content = add_unit(content, "0x60C2", "µs")

with open(path, 'w', encoding='utf-8') as f:
    f.write(content)
print("Done Python replace!")
