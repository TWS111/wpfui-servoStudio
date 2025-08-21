// This Source Code Form is subject to the terms of the MIT License.
// If a copy of the MIT was not distributed with this file, You can obtain one at https://opensource.org/licenses/MIT.
// Copyright (C) Leszek Pomianowski and WPF UI Contributors.
// All Rights Reserved.

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using Windows.ApplicationModel.VoiceCommands;
using RJCP.IO.Ports;
using Wpf.Ui.Abstractions.Controls;
using Wpf.Ui.servoStudio.Models;

namespace Wpf.Ui.servoStudio.ViewModels;

public abstract class ViewModel : 
    ObservableObject, INavigationAware
{
    public SerialPortStream vcom = new SerialPortStream();
    private StateEnum State = StateEnum.Init;

    public void OnThreadStart()
    {
        Thread checkComBuffer = new Thread(new ThreadStart(CheckingComBuffer));
        checkComBuffer.Start();
        Thread autoSendReadFrame = new Thread(new ThreadStart(AutoSendReadFrame));
        autoSendReadFrame.Start();
    }    

    public void PortEventQueue()
    {
        switch (State)
        {
            case StateEnum.Init:
                break;
            
            case StateEnum.InsertToSendGroup1:
                // Insert logic for sending group 1
                State = StateEnum.WaitToInsertReceiveGroup1;
                break;

            case StateEnum.WaitToInsertReceiveGroup1:

                break;

        }
    }

    public void AutoSendReadFrame()
    {
        while (true)
        {
            if(Flags.isAutoSendReadFrameOn)
            {
                if (vcom.IsOpen)
                {
                    try
                    {

                    }
                    catch (Exception)
                    {
                       //
                    }
                }
            }
        }
    }
    public void CheckingComBuffer()
    {
        int bufLength = 0;
        int slaveAddr = 0x03;
        while (true)
        {
            if (!vcom.IsOpen) continue;  //串口被意外关闭
            try
            {
                int _temp = vcom.BytesToRead;
                string receiveText = string.Empty;
                if (_temp > 0)
                {
                    byte[] __buf1 = new byte[3];
                    vcom.Read(__buf1, 0, 3);
                    if (__buf1[0].ToString("X2") != slaveAddr.ToString("X2"))
                        continue;
                    if (__buf1[1].ToString("X2") == "83" || __buf1[1].ToString("X2") == "90")
                    {
                        bufLength = 2;
                        //errorCode = __buf1[2];
                        //errorJudge();
                    }

                    else
                        bufLength = HEXConvert.HEXConvertToByte(__buf1[2].ToString("X2"), 2);
                    if (bufLength < 4)
                    {
                        continue;
                        vcom.Flush();
                    }
                    //else if (bufLength == 12)
                    //{
                    //    recvFlag = 1;
                    //}
                    //else if (bufLength == 2 * paraNum)
                    //{
                    //    recvFlag = 2;
                    //}
                    //else
                    //{
                    //    vcom.Flush();
                    //    continue;
                    //}

                    byte[] __buf = new byte[bufLength + 2];
                    vcom.Read(__buf, 0, bufLength + 2);
                    try
                    {
                        for (int i = 0; i < bufLength; i++)
                        {
                            receiveText += __buf[i].ToString("X2");
                            //Console.WriteLine(RECVtest);
                        }

                        //judgetest(RECVtest);
                        ////label1.Text = RECVtest;
                        //RECVtest = null;
                    }
                    catch (Exception)
                    {
                        //MessageBox.Show("下位机已断开", "提示");
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        __buf[i] = 0;
                    }
                }
            }
            catch
            {
                
                continue;
            }
        }
    }
    public FactoryParametersFillInfo factoryParametersFill = new FactoryParametersFillInfo();
    /// <inheritdoc />
    public virtual Task OnNavigatedToAsync()
    {
        OnNavigatedTo();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the event that is fired after the component is navigated to.
    /// </summary>
    // ReSharper disable once MemberCanBeProtected.Global
    public virtual void OnNavigatedTo() { }

    /// <inheritdoc />
    public virtual Task OnNavigatedFromAsync()
    {
        OnNavigatedFrom();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles the event that is fired before the component is navigated from.
    /// </summary>
    // ReSharper disable once MemberCanBeProtected.Global
    public virtual void OnNavigatedFrom() { }

    public event PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChangedEventHandler handler = this.PropertyChanged;
        if (handler != null)
        {
            handler(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
