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
using System.Drawing.Imaging.Effects;

namespace Wpf.Ui.servoStudio.ViewModels;

public abstract class ViewModel :
    ObservableObject, INavigationAware
{
    public SerialPortStream vcom = new SerialPortStream();
    public SerialPortFaultInfo PortFaultInfo = new SerialPortFaultInfo();
    public SerialPortTransmitInfo PortTransmitInfo = new SerialPortTransmitInfo();
    public SerialPortReceiveInfo PortReceiveInfo = new SerialPortReceiveInfo();
    public SerialPortReceiveInfo PortReadyReceive = new SerialPortReceiveInfo();
    public QueueInsertInfo QueueInfo = new QueueInsertInfo();
    public QueueInfo QueueListSample = new QueueInfo();
    public SerialPortFrameInfo PortFrameInfo = new SerialPortFrameInfo();

    public StateEnum State = StateEnum.Init;

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

    public async Task CheckDeviceAddress()
    {
        while(State == StateEnum.CheckDeviceInfo)
        {
            
        }
    }

    public void AutoSendReadFrame()
    {
        while (true)
        {
            if (Flags.isAutoSendReadFrameOn && vcom.IsOpen)
            {
                if (State == StateEnum.Init)
                {
                    State = StateEnum.CheckDeviceInfo;
                    continue;
                }
                else if (State == StateEnum.CheckDeviceInfo)
                {
                    State = StateEnum.WaitToReceiveDeviceInfo;
                    if(PortTransmitInfo.slaveAddress >= 0xFF)
                    {
                        PortTransmitInfo.slaveAddress = 0;
                    }
                    
                    continue;
                }

                else if ((int)State >= 3 && (int)State % 2 == 0)
                {
                    continue;
                }
                else if ((int)State >= 3 && ((int)State - 3) % 4 == 2)
                {
                    int tempWeightCountSet = QueueListSample.groupWeightSet[((int)State - 3) / 4];
                    if (tempWeightCountSet <= 0 || QueueListSample.groupWeightActual[((int)State - 3) / 4] >= tempWeightCountSet)
                    {
                        QueueListSample.groupWeightActual[((int)State - 3) / 4] = 0;
                        continue;
                    }
                    else
                    {
                        QueueListSample.groupWeightActual[((int)State - 3) / 4]++;

                    }

                }
                else if ((int)State >= 3 && ((int)State - 3) % 4 == 0)
                {
                    if (QueueInfo.isInsertToSendQueue)
                    {
                        QueueInfo.isInsertToSendQueue = false;
                    }
                }

            }
        }
    }

    public void InsertToQueueFromBufferList()
    {
        while (true)
        {
            if (!QueueInfo.isInsertToSendQueue)
            {
                if (QueueListSample.bufferTransmitDataList.Count > 0)
                {
                    int queueLength = QueueListSample.bufferTransmitDataList.Count;
                    QueueInfo.slaveAddress = QueueListSample.bufferTransmitDataList[queueLength].slaveAddress;
                    QueueInfo.insertGroupNumber = QueueListSample.bufferTransmitDataList[queueLength].insertGroupNumber;
                    QueueInfo.insertCommand = QueueListSample.bufferTransmitDataList[queueLength].insertCommand;
                    QueueInfo.insertRegisterIndex = QueueListSample.bufferTransmitDataList[queueLength].insertRegisterIndex;
                    QueueInfo.insertRegisterLength = QueueListSample.bufferTransmitDataList[queueLength].insertRegisterLength;
                    QueueInfo.bufferTransmitData = QueueListSample.bufferTransmitDataList[queueLength].bufferTransmitData;
                    QueueListSample.bufferTransmitDataList.RemoveAt(queueLength);

                    QueueInfo.isInsertToSendQueue = true;
                }
            }
        }
    }
    public void CheckingComBuffer()
    {
        int slaveAddress = 0x03;
        while (true)
        {
            if (!vcom.IsOpen) continue;  //串口被意外关闭
            if ((int)State < 2 || (int)State % 2 == 1) continue;
            try
            {
                if (PortFrameInfo.isNotReadyToWaitEnoughLength)
                {
                    //make sure the next read is a complete frame
                    PortFrameInfo.bufferRemain = vcom.BytesToRead;
                    if (PortFrameInfo.bufferRemain < PortFrameInfo.readyToReadLength)
                    {
                        continue;
                    }
                    else
                    {
                        vcom.Read(PortReceiveInfo.bufferReceiveData, 0, PortFrameInfo.readyToReadLength);
                        if (PortReceiveInfo.bufferReceiveData[0] != PortTransmitInfo.registerIndex[0] && PortReceiveInfo.bufferReceiveData[1] != PortTransmitInfo.registerIndex[1])
                        {
                            PortFaultInfo.faultCount[(int)SerialPortTransmitFault.WrongRegisterNumber]++;
                            PortReadyReceive.isDataAvailable = false;
                        }
                        else
                        {
                            for (int i = 0; i < PortFrameInfo.readyToReadLength; i++)
                            {
                                PortReadyReceive.bufferReceiveData[i] = PortReceiveInfo.bufferReceiveData[i];
                            }

                            PortReadyReceive.isDataAvailable = true;
                            if(State < StateEnum.EOE - 2)
                            {
                                State += 1;
                            }
                            else
                            {
                                State = StateEnum.ReadyToSendGroup1;
                            }
                        }

                        PortFrameInfo.readyToReadLength = 0;
                        PortFrameInfo.isNotReadyToWaitEnoughLength = false;
                    }
                }
                else
                {
                    int bufferRemain = vcom.BytesToRead;
                    string receiveText = string.Empty;
                    if (bufferRemain > 0)
                    {
                        vcom.Read(PortReceiveInfo.headReceiveData, 0, 3);
                        if (PortReceiveInfo.headReceiveData[0].ToString("X2") != slaveAddress.ToString("X2"))
                        {
                            vcom.Flush();
                            continue;
                        }

                        if (PortReceiveInfo.headReceiveData[1] == 0x83 || PortReceiveInfo.headReceiveData[1] == 0x86 || PortReceiveInfo.headReceiveData[1] == 0x90)
                        {
                            PortFrameInfo.readyToReadLength = 4;
                            PortFaultInfo.faultName = (SerialPortTransmitFault)PortReceiveInfo.headReceiveData[2];
                        }
                        else
                        {
                            PortFrameInfo.readyToReadLength = PortReceiveInfo.headReceiveData[2] + 2;
                        }

                        int bufferDataRemain = vcom.BytesToRead;
                        if (bufferDataRemain < PortFrameInfo.readyToReadLength)
                        {
                            PortFrameInfo.isNotReadyToWaitEnoughLength = true;
                            continue;
                        }

                        vcom.Read(PortReceiveInfo.bufferReceiveData, 0, PortFrameInfo.readyToReadLength);
                        PortFrameInfo.readyToReadLength = 0;
                    }
                }
            }
            catch
            {
                //
                continue;
            }
        }
    }

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
}
