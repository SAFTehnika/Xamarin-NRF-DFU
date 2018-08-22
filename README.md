# Xamarin-NRF-DFU

Simple plugin for Xamarin forms which supports Secure DFU update.
## Platforms

 > iOS, Android, and basically any other what is supported by aritchie BLE plugin
## Prerequesites

- Bluetooth LE plugin by aritchie https://github.com/aritchie/bluetoothle
- Nordic semiconductor device
## Features
 - Upload firmware for NordicSemiconductor devices supporting Buttonless DFU without bonds

## Usage
Include
		

    using Plugin.XamarinNordicDFU;
Initialize
    
    dfu = new DFU(true);
    DFUEvents.OnFimwareProgressChanged = (float progress, TimeSpan elapsed) =>
    {
    
    };
    DFUEvents.OnSuccess = (TimeSpan elapsed) =>
    {
    
    };
    DFUEvents.OnError = (string error) =>
    {
    
    };
    DFUEvents.OnResponseError = (ResponseErrors error) =>
    {
    
    };
    DFUEvents.OnExtendedError = (ExtendedErrors error) =>
    {
    
    };

Run

    var assembly = System.Reflection.Assembly.GetExecutingAssembly();

	#if __IOS__
            var currentOS = "iOS";
	#endif
	#if __ANDROID__
            var currentOS = "Droid";
	#endif
    string firmwarePacketPath = String.Format("MyHelloProject.{0}.FW.{1}", currentOS, "nrf52832_xxaa.bin");
    string initPacketPath = String.Format("MyHelloProject.{0}.FW.{1}", currentOS, "nrf52832_xxaa.dat");

    
    Stream FirmwarePacket = assembly.GetManifestResourceStream(firmwarePacketPath);
    Stream InitPacket = assembly.GetManifestResourceStream(initPacketPath);
                
    await dfu.Start(Device.DeviceObject, FirmwarePacket, InitPacket);

## TODO

 * Nuget package
 * Other firmware upgrade possibilities. Other than buttonless without bonds
## License
 > MIT
 
