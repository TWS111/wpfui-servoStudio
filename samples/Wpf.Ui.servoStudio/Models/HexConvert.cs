// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

namespace Wpf.Ui.servoStudio.Models;

public class HEXConvert
{
    public static byte HEXConvertToByte(string str, int count)
    {
        int value = 0;
        int multi = 1;
        for (int i = count - 1; i >= 0; i--)
        {
            switch (str.Substring(i, 1))
            {
                case "0": break;
                case "1": value += 1 * multi; break;
                case "2": value += 2 * multi; break;
                case "3": value += 3 * multi; break;
                case "4": value += 4 * multi; break;
                case "5": value += 5 * multi; break;
                case "6": value += 6 * multi; break;
                case "7": value += 7 * multi; break;
                case "8": value += 8 * multi; break;
                case "9": value += 9 * multi; break;
                case "A": value += 10 * multi; break;
                case "B": value += 11 * multi; break;
                case "C": value += 12 * multi; break;
                case "D": value += 13 * multi; break;
                case "E": value += 14 * multi; break;
                case "F": value += 15 * multi; break;
            }
            multi *= 16;
        }
        return byte.Parse(value.ToString());
    }
    public static double HEXConvertToDouble(string str, int count)
    {
        double value = 0;
        double multi = 1;
        for (int i = count - 1; i >= 0; i--)
        {
            switch (str.Substring(i, 1))
            {
                case "0": break;
                case "1": value += 1 * multi; break;
                case "2": value += 2 * multi; break;
                case "3": value += 3 * multi; break;
                case "4": value += 4 * multi; break;
                case "5": value += 5 * multi; break;
                case "6": value += 6 * multi; break;
                case "7": value += 7 * multi; break;
                case "8": value += 8 * multi; break;
                case "9": value += 9 * multi; break;
                case "A": value += 10 * multi; break;
                case "B": value += 11 * multi; break;
                case "C": value += 12 * multi; break;
                case "D": value += 13 * multi; break;
                case "E": value += 14 * multi; break;
                case "F": value += 15 * multi; break;
            }
            multi *= 16;
        }
        return value;
    }

    public static double HEXConvertToInt32(string str, int count)
    {
        double value = 0;
        double multi = 1;
        for (int i = count - 1; i >= 0; i--)
        {

            switch (str.Substring(i, 1))
            {
                case "0": break;
                case "1": value += 1 * multi; break;
                case "2": value += 2 * multi; break;
                case "3": value += 3 * multi; break;
                case "4": value += 4 * multi; break;
                case "5": value += 5 * multi; break;
                case "6": value += 6 * multi; break;
                case "7": value += 7 * multi; break;
                case "8" when i != 0: value += 8 * multi; break;
                case "9" when i != 0: value += 9 * multi; break;
                case "A" when i != 0: value += 10 * multi; break;
                case "B" when i != 0: value += 11 * multi; break;
                case "C" when i != 0: value += 12 * multi; break;
                case "D" when i != 0: value += 13 * multi; break;
                case "E" when i != 0: value += 14 * multi; break;
                case "F" when i != 0: value += 15 * multi; break;
                case "8" when i == 0: value -= 0x80000000; break;
                case "9" when i == 0: value -= 0x80000000; value += 1 * multi; break;
                case "A" when i == 0: value -= 0x80000000; value += 2 * multi; break;
                case "B" when i == 0: value -= 0x80000000; value += 3 * multi; break;
                case "C" when i == 0: value -= 0x80000000; value += 4 * multi; break;
                case "D" when i == 0: value -= 0x80000000; value += 5 * multi; break;
                case "E" when i == 0: value -= 0x80000000; value += 6 * multi; break;
                case "F" when i == 0: value -= 0x80000000; value += 7 * multi; break;
            }
            multi *= 16;
        }
        return value;
    }
}
