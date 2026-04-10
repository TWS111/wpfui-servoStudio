import sys

path = r"samples\Wpf.Ui.servoStudio\Views\Pages\MotionPages\MotionTypePage.xaml"
with open(path, 'r', encoding='utf-8') as f:
    lines = f.readlines()

units = {
    '0x607A': 'inc',
    '0x6081': 'inc/s',
    '0x6083': 'inc/s²',
    '0x6084': 'inc/s²',
    '0x6085': 'inc/s²',
    '0x60FF': 'inc/s',
    '0x6071': '‰',
    '0x6072': '‰',
    '0x6087': '‰/s',
    '0x6099-1': 'inc/s',
    '0x6099-2': 'inc/s',
    '0x609A': 'inc/s²',
    '0x60B0': 'inc',
    '0x60B1': 'inc/s',
    '0x60B2': '‰',
    '0x60C2': 'µs'
}

new_lines = []
i = 0
while i < len(lines):
    line = lines[i]
    if 'MinWidth="260" MaxWidth="420"' in line:
        line = line.replace('MinWidth="260" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"')
    if 'MinWidth="220" MaxWidth="420"' in line:
        line = line.replace('MinWidth="220" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"')
    
    new_lines.append(line)
    
    # Check if it's the TextBlock Label row
    modified_line = False
    for key, unit in units.items():
        if f'[{key}]:' in line and 'Style="{StaticResource LabelStyle}"' in line:
            # The next line should be ui:NumberBox
            if i + 1 < len(lines):
                next_line = lines[i+1]
                if '<ui:NumberBox' in next_line:
                    if 'MinWidth="260" MaxWidth="420"' in next_line:
                        next_line = next_line.replace('MinWidth="260" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"')
                    if 'MinWidth="220" MaxWidth="420"' in next_line:
                        next_line = next_line.replace('MinWidth="220" MaxWidth="420"', 'MinWidth="200" MaxWidth="260"')
                    new_lines.append(next_line)
                    
                    # Insert Unit TextBlock here
                    # Indent is typically 28 spaces for this file
                    indent = " " * 28
                    new_lines.append(f'{indent}<TextBlock Text="{unit}" VerticalAlignment="Center" Margin="12,0,0,0" Width="45" FontSize="15" Foreground="{{DynamicResource TextFillColorSecondaryBrush}}"/>\n')
                    
                    # Modify the next line if it is Slider
                    if i + 2 < len(lines):
                        next2 = lines[i+2]
                        if '<Slider' in next2:
                            if 'Margin="12,0,0,0"' in next2:
                                next2 = next2.replace('Margin="12,0,0,0"', 'Margin="12,0,32,0" MaxWidth="300"')
                            else:
                                next2 = next2.replace('Margin="12,0,0,0"', 'Margin="12,0,32,0" MaxWidth="300"')
                            
                            new_lines.append(next2)
                            i += 2
                            modified_line = True
                            break
    
    # if we jumped ahead, we handled those lines
    if modified_line:
        pass # already incremented i
    i += 1

with open(path, 'w', encoding='utf-8') as f:
    f.writelines(new_lines)

print("Safely replaced.")
