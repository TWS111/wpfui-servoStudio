// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Wpf.Ui.servoStudio.Models;

internal class CRC16_modbus
{    
    public static class CRC16
    {
        /// <summary>
        /// CRC校验，参数data为byte数组
        /// </summary>
        /// <param name="data">校验数据，字节数组</param>
        /// <returns>字节0是高8位，字节1是低8位</returns>
        public static byte[] CRCCalc(byte[] data)
        {
            //crc计算赋初始值
            int crc = 0xffff;
            for (int i = 0; i < data.Length; i++)
            {
                crc = crc ^ data[i];
                for (int j = 0; j < 8; j++)
                {
                    int temp;
                    temp = crc & 1;
                    crc = crc >> 1;
                    crc = crc & 0x7fff;
                    if (temp == 1)
                    {
                        crc = crc ^ 0xa001;
                    }
                    crc = crc & 0xffff;
                }
            }
            //CRC寄存器的高低位进行互换
            byte[] crc16 = new byte[2];
            //CRC寄存器的高8位变成低8位，
            crc16[1] = (byte)((crc >> 8) & 0xff);
            //CRC寄存器的低8位变成高8位
            crc16[0] = (byte)(crc & 0xff);
            return crc16;
        }

        /// <summary>
        /// CRC校验，参数为空格或逗号间隔的字符串
        /// </summary>
        /// <param name="data">校验数据，逗号或空格间隔的16进制字符串(带有0x或0X也可以),逗号与空格不能混用</param>
        /// <returns>字节0是高8位，字节1是低8位</returns>
        public static byte[] CRCCalc(string data)
        {
            //分隔符是空格还是逗号进行分类，并去除输入字符串中的多余空格
            IEnumerable<string> datac = data.Contains(",") ? data.Replace(" ", "").Replace("0x", "").Replace("0X", "").Trim().Split(',') : data.Replace("0x", "").Replace("0X", "").Split(' ').ToList().Where(u => u != "");
            List<byte> bytedata = new List<byte>();
            foreach (string str in datac)
            {
                bytedata.Add(byte.Parse(str, System.Globalization.NumberStyles.AllowHexSpecifier));
            }
            byte[] crcbuf = bytedata.ToArray();
            //crc计算赋初始值
            return CRCCalc(crcbuf);
        }

        /// <summary>
        ///  CRC校验，截取data中的一段进行CRC16校验
        /// </summary>
        /// <param name="data">校验数据，字节数组</param>
        /// <param name="offset">从头开始偏移几个byte</param>
        /// <param name="length">偏移后取几个字节byte</param>
        /// <returns>字节0是高8位，字节1是低8位</returns>
        public static byte[] CRCCalc(byte[] data, int offset, int length)
        {
            byte[] Tdata = data.Skip(offset).Take(length).ToArray();
            return CRCCalc(Tdata);
        }
    }
}
