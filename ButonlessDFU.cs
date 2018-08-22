// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;
using System.Threading.Tasks;
using Plugin.BluetoothLE;
using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Text;
using System.Linq;

namespace Plugin.XamarinNordicDFU
{
    class DFUTimeout : Exception
    {

    }
    partial class DFU
    {
        /// <summary>
        /// Switch device in Secure DFU state
        /// ! IMPORTANT, assumes that each device has different name !
        /// </summary>
        /// <param name="device"></param>
        /// <returns>Device which is in buttonles state</returns>
        private async Task<IDevice> ButtonlessDFUWithoutBondsToSecureDFU(IDevice device)
        {
            Debug.WriteLineIf(LogLevelDebug, String.Format("Start of Buttonless switching"));

            //await RefreshGattAsync(device);
            
            IGattCharacteristic buttonlessCharacteristic = null;
            TaskCompletionSource<CharacteristicGattResult> notif = null;

            device.Connect();
            device = await device.ConnectWait().Timeout(DeviceConnectionTimeout);
                    
            buttonlessCharacteristic = await device.GetKnownCharacteristics(DfuService, DfuButtonlessCharacteristicWithoutBonds).Timeout(OperationTimeout);
            await buttonlessCharacteristic.EnableNotifications(true).Timeout(OperationTimeout);

            // Change device Name

            // Advertisment name should not be longer than 20 symbols
            var newFullName = device.Name + "DFU";
            var newName = new string(newFullName.Take(20).ToArray());
            byte[] name = Encoding.ASCII.GetBytes(newName);
            int newNameLen = name.Length;
            byte[] newNameCommand = new byte[newNameLen + 1 + 1];
            newNameCommand[0] = CChangeAdvertisedName;
            newNameCommand[1] = (byte)(uint)newNameLen;
            name.CopyTo(newNameCommand, 2);

            var nameChangeNotif = GetTimedNotification(buttonlessCharacteristic);
            await buttonlessCharacteristic.Write(newNameCommand).Timeout(OperationTimeout);
            var nameChangeResult = await nameChangeNotif.Task;
            Debug.WriteLineIf(LogLevelDebug, String.Format("Device name change response {0}", nameChangeResult.Data != null ? BitConverter.ToString(nameChangeResult.Data) : "Empty"));

            // Jump from the main application to Secure DFU bootloader (Secure DFU mode)
            notif = GetTimedNotification(buttonlessCharacteristic);
            await buttonlessCharacteristic.Write(CEnterDFU).Timeout(OperationTimeout);
            var result = await notif.Task;
            Debug.WriteLineIf(LogLevelDebug, String.Format("Restart response {0}", result.Data != null ? BitConverter.ToString(result.Data) : "Empty"));

            await buttonlessCharacteristic.DisableNotifications();
            /*
			* The response received from the DFU device contains:
			* +---------+--------+----------------------------------------------------+
			* | byte no | value  | description                                        |
			* +---------+--------+----------------------------------------------------+
			* | 0       | 0x20   | Response code                                      |
			* | 1       | 0x01   | The Op Code of a request that this response is for |
			* | 2       | STATUS | Status code                                        |
			* +---------+--------+----------------------------------------------------+
			*/
            int status = result.Data[2];
            if (status != ButtonlessSwitchSuccessCode)
            {
                throw new Exception("Init status not correct " + status);
            }

            
            bool alreadyRestarted = false;
            IDisposable dispose = null;
            dispose = device.WhenStatusChanged().Subscribe(res =>
            {
                if(res == ConnectionStatus.Disconnected || res == ConnectionStatus.Disconnecting)
                {
                    alreadyRestarted = true;
                }
                Debug.WriteLine("###################### STAT {0}", res);
            });
            
            
            dispose?.Dispose();
            if (!alreadyRestarted)
            {
                await device.WhenDisconnected().Take(1).Timeout(DeviceRestartTimeout);
                device.CancelConnection();
            }

            IDevice newDevice = await ScanDFUDevice(device, newName);
            Debug.WriteLineIf(LogLevelDebug, String.Format("End of Buttonless switching"));

            return newDevice;
        }
        private async Task<IDevice> ScanDFUDevice(IDevice device, string newName)
        {
            
            IDevice newDevice = null;
            IDisposable scanner = null;
            bool DeviceFound = false;
            object locked = new object();
            var tcs = new TaskCompletionSource<bool>();

            Task task = Task.Delay(DeviceRestartTimeout).ContinueWith(t => {
                lock (locked)
                {
                    if (!DeviceFound)
                    {
                        scanner?.Dispose();
                        tcs.TrySetException(new TimeoutException());
                    }
                }
            });

            // Scan new Device which is (MAC adress + 1)
            scanner = CrossBleAdapter.Current.Scan().Subscribe(scanResult =>
            {
                Debug.WriteLineIf(LogLevelDebug, String.Format("Scanned device: Name: |{0}:{1}| UUID: {2} preuid: {3}, compareRes: {4}",device.Name, scanResult.Device.Name, scanResult.Device.Uuid,device.Uuid, device.Uuid.CompareTo(scanResult.Device.Uuid)));
                // If name is the same but id is different, should be our device
                if (newName == scanResult.Device.Name)
                {
                    //if (device.Uuid.CompareTo(scanResult.Device.Uuid) != 0)
                    //{
                        Debug.WriteLineIf(LogLevelDebug, String.Format("Device found: Name: {0} UUID: {1}", scanResult.Device.Name, scanResult.Device.Uuid));
                        lock (locked)
                        {
                            DeviceFound = true;
                        }
                        scanner?.Dispose();
                        newDevice = scanResult.Device;
                        tcs.TrySetResult(true);
                    //}
                }
                else if (device.Name == scanResult.Device.Name)
                {
                    if (device.Uuid.CompareTo(scanResult.Device.Uuid) != 0)
                    {
                        Debug.WriteLineIf(LogLevelDebug, String.Format("Device found: Name: {0} UUID: {1}", scanResult.Device.Name, scanResult.Device.Uuid));
                        lock (locked)
                        {
                            DeviceFound = true;
                        }
                        scanner?.Dispose();
                        newDevice = scanResult.Device;
                        tcs.TrySetResult(true);
                    }
                }
            });
            await tcs.Task;
            return newDevice;
        }
    }
}
