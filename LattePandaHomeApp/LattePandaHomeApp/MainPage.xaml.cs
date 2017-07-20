using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using System.Collections.ObjectModel;
using Windows.Devices.Enumeration;
using Windows.Devices.SerialCommunication;
using Windows.Storage.Streams;
using System.Threading;
using System.Threading.Tasks;

// Azure IoTHub Device Client SDK
using Microsoft.Azure.Devices.Client;
// Required for Json Format Handling
using System.Runtime.Serialization.Json;

// Required for Azure Storage
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using Microsoft.WindowsAzure.Storage.Table;
using System.Net.Http;
// For Threading timer
using Windows.System.Threading;
// For text encoding
using System.Text;
using System.Diagnostics;
using Windows.UI.Popups;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=402352&clcid=0x409

namespace LattePandaHomeApp
{
    public class SensorEntity : TableEntity
    {
        public SensorEntity()
        {
            this.PartitionKey = "LattePanda";
            this.RowKey = Guid.NewGuid().ToString();

            MeasurementTime = DateTime.Now;
            Node = 0;
            Gas = 0;          
            Light = 0;
            HumanDetection = 0;
            Temperature = 0;
        }
        public DateTime MeasurementTime { get; set; }
        public int Node { get; set; }
        public int Gas { get; set; }
        public int Light { get; set; }
        public int HumanDetection { get; set; }
        public int Temperature { get; set; }
    }
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainPage : Page
    {
        private SerialDevice serialPort = null;
        DataWriter dataWriteObject = null;
        DataReader dataReaderObject = null;

        private ObservableCollection<DeviceInformation> listOfDevices;
        private CancellationTokenSource ReadCancellationTokenSource;

        //temporary data
        private int nodeValue = 0;
        private int gasValue = 0;
        private int lightValue = 0;
        private int humanDetectionValue = 0;
        private int temperatureValue = 0;

        //transmit timer to Storage Table
        private static ThreadPoolTimer timerDataTransfer;

        //transmit timer to IotHub
        private static ThreadPoolTimer timerIotHubTransfer;

        //connect string
        private const string connectionstring ="HostName=<Hostname>.azure-devices.net;DeviceId=<Device ID created through APP or Device Explorer>;SharedAccessKey= Shared access key of Device Explorer. Copy from Device Explorer and Paste it here";

        public MainPage()
        {
            this.InitializeComponent();
            comPortInput.IsEnabled = false;
            sendTextButton.IsEnabled = false;
            listOfDevices = new ObservableCollection<DeviceInformation>();
            ListAvailablePorts();
        }

        /// <summary>
        /// ListAvailablePorts
        /// - Use SerialDevice.GetDeviceSelector to enumerate all serial devices
        /// - Attaches the DeviceInformation to the ListBox source so that DeviceIds are displayed
        /// </summary>
        private async void ListAvailablePorts()
        {
            try
            {
                string aqs = SerialDevice.GetDeviceSelector();
                var dis = await DeviceInformation.FindAllAsync(aqs);

                status.Text = "Select a device and connect";

                for (int i = 0; i < dis.Count; i++)
                {
                    listOfDevices.Add(dis[i]);
                }

                DeviceListSource.Source = listOfDevices;
                comPortInput.IsEnabled = true;
                ConnectDevices.SelectedIndex = -1;
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        /// <summary>
        /// comPortInput_Click: Action to take when 'Connect' button is clicked
        /// - Get the selected device index and use Id to create the SerialDevice object
        /// - Configure default settings for the serial port
        /// - Create the ReadCancellationTokenSource token
        /// - Start listening on the serial port input
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void comPortInput_Click(object sender, RoutedEventArgs e)
        {
            var selection = ConnectDevices.SelectedItems;

            if (selection.Count <= 0)
            {
                status.Text = "Select a device and connect";
                return;
            }

            DeviceInformation entry = (DeviceInformation)selection[0];

            try
            {
                serialPort = await SerialDevice.FromIdAsync(entry.Id);

                // Disable the 'Connect' button 
                comPortInput.IsEnabled = false;

                // Configure serial settings
                serialPort.WriteTimeout = TimeSpan.FromMilliseconds(1000);
                serialPort.ReadTimeout = TimeSpan.FromMilliseconds(1000);
                serialPort.BaudRate = 115200;
                serialPort.Parity = SerialParity.None;
                serialPort.StopBits = SerialStopBitCount.One;
                serialPort.DataBits = 8;
                serialPort.Handshake = SerialHandshake.None;

                // Display configured settings
                status.Text = "Serial port configured successfully: ";
                status.Text += serialPort.BaudRate + "-";
                status.Text += serialPort.DataBits + "-";
                status.Text += serialPort.Parity.ToString() + "-";
                status.Text += serialPort.StopBits;

                // Set the RcvdText field to invoke the TextChanged callback
                // The callback launches an async Read task to wait for data
                rcvdText.Text = "Waiting for data...";

                // Create cancellation token object to close I/O operations when closing the device
                ReadCancellationTokenSource = new CancellationTokenSource();

                // Enable 'WRITE' button to allow sending data
                sendTextButton.IsEnabled = true;

                //Enable send to Azure Button
                IotHubButton.IsEnabled = true;
                AzureStorageButton.IsEnabled = true;

                Listen();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
                comPortInput.IsEnabled = true;
                sendTextButton.IsEnabled = false;
                AzureStorageButton.IsEnabled = false;
                IotHubButton.IsEnabled = false;
            }
        }

        /// <summary>
        /// sendTextButton_Click: Action to take when 'WRITE' button is clicked
        /// - Create a DataWriter object with the OutputStream of the SerialDevice
        /// - Create an async task that performs the write operation
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void sendTextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (serialPort != null)
                {
                    // Create the DataWriter object and attach to OutputStream
                    dataWriteObject = new DataWriter(serialPort.OutputStream);

                    //Launch the WriteAsync task to perform the write
                    await WriteAsync();
                }
                else
                {
                    status.Text = "Select a device and connect";
                }
            }
            catch (Exception ex)
            {
                status.Text = "sendTextButton_Click: " + ex.Message;
            }
            finally
            {
                // Cleanup once complete
                if (dataWriteObject != null)
                {
                    dataWriteObject.DetachStream();
                    dataWriteObject = null;
                }
            }
        }

        /// <summary>
        /// WriteAsync: Task that asynchronously writes data from the input text box 'sendText' to the OutputStream 
        /// </summary>
        /// <returns></returns>
        private async Task WriteAsync()
        {
            Task<UInt32> storeAsyncTask;

            if (sendText.Text.Length != 0)
            {
                // Load the text from the sendText input text box to the dataWriter object
                dataWriteObject.WriteString(sendText.Text);

                // Launch an async task to complete the write operation
                storeAsyncTask = dataWriteObject.StoreAsync().AsTask();

                UInt32 bytesWritten = await storeAsyncTask;
                if (bytesWritten > 0)
                {
                    status.Text = sendText.Text + ", ";
                    status.Text += "bytes written successfully!";
                }
                sendText.Text = "";
            }
            else
            {
                status.Text = "Enter the text you want to write and then click on 'WRITE'";
            }
        }

        /// <summary>
        /// - Create a DataReader object
        /// - Create an async task to read from the SerialDevice InputStream
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void Listen()
        {
            try
            {
                if (serialPort != null)
                {
                    dataReaderObject = new DataReader(serialPort.InputStream);

                    // keep reading the serial input
                    while (true)
                    {
                        await ReadAsync(ReadCancellationTokenSource.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex.GetType().Name == "TaskCanceledException")
                {
                    status.Text = "Reading task was cancelled, closing device and cleaning up";
                    CloseDevice();
                }
                else
                {
                    status.Text = ex.Message;
                }
            }
            finally
            {
                // Cleanup once complete
                if (dataReaderObject != null)
                {
                    dataReaderObject.DetachStream();
                    dataReaderObject = null;
                }
            }
        }

        /// <summary>
        /// ReadAsync: Task that waits on data and reads asynchronously from the serial device InputStream
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        private async Task ReadAsync(CancellationToken cancellationToken)
        {
            Task<UInt32> loadAsyncTask;

            uint ReadBufferLength = 1024;

            // If task cancellation was requested, comply
            cancellationToken.ThrowIfCancellationRequested();

            // Set InputStreamOptions to complete the asynchronous read operation when one or more bytes is available
            dataReaderObject.InputStreamOptions = InputStreamOptions.Partial;

            // Create a task object to wait for data on the serialPort.InputStream
            loadAsyncTask = dataReaderObject.LoadAsync(ReadBufferLength).AsTask(cancellationToken);

            // Launch the task and wait
            UInt32 bytesRead = await loadAsyncTask;
            if (bytesRead > 0)
            {
                rcvdText.Text = dataReaderObject.ReadString(bytesRead);
                rcvdNode.Text= rcvdText.Text.Substring(4, 1);
                nodeValue= Convert.ToInt32(rcvdNode.Text);
                rcvdGas.Text = rcvdText.Text.Substring(2, 2);
                gasValue = Convert.ToInt32(rcvdGas.Text);
                rcvdLight.Text = rcvdText.Text.Substring(6, 2);
                lightValue = 15;
                rcvdHuman.Text = rcvdText.Text.Substring(10, 1);
                humanDetectionValue = Convert.ToInt32(rcvdHuman.Text);
                rcvdTemp.Text = rcvdText.Text.Substring(13, 2);
                temperatureValue = Convert.ToInt32(rcvdTemp.Text);
                status.Text = "bytes read successfully!";
            }
        }

        /// <summary>
        /// CancelReadTask:
        /// - Uses the ReadCancellationTokenSource to cancel read operations
        /// </summary>
        private void CancelReadTask()
        {
            if (ReadCancellationTokenSource != null)
            {
                if (!ReadCancellationTokenSource.IsCancellationRequested)
                {
                    ReadCancellationTokenSource.Cancel();
                }
            }
        }

        /// <summary>
        /// CloseDevice:
        /// - Disposes SerialDevice object
        /// - Clears the enumerated device Id list
        /// </summary>
        private void CloseDevice()
        {
            if (serialPort != null)
            {
                serialPort.Dispose();
            }
            serialPort = null;

            comPortInput.IsEnabled = true;
            sendTextButton.IsEnabled = false;
            IotHubButton.IsEnabled = false;
            AzureStorageButton.IsEnabled = false;
            rcvdText.Text = "";
            listOfDevices.Clear();
        }

        /// <summary>
        /// closeDevice_Click: Action to take when 'Disconnect and Refresh List' is clicked on
        /// - Cancel all read operations
        /// - Close and dispose the SerialDevice object
        /// - Enumerate connected devices
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void closeDevice_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                status.Text = "";
                CancelReadTask();
                CloseDevice();
                ListAvailablePorts();
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
        }

        private void IoTButton_Click(object sender, RoutedEventArgs e)
        {
             timerIotHubTransfer = ThreadPoolTimer.CreatePeriodicTimer(dataIoTHubTick, TimeSpan.FromMilliseconds(Convert.ToInt32(5000)));
        }

        private async void dataIoTHubTick(ThreadPoolTimer timer)
        {
            try
            {
                // Create a new customer entity.
                SensorEntity ent = new SensorEntity();

                ent.MeasurementTime = DateTime.Now;
                ent.Node = nodeValue;
                ent.Gas = gasValue;
                ent.Light =lightValue;
                ent.HumanDetection = humanDetectionValue;
                ent.Temperature =temperatureValue;

                String JsonData = Serialize(ent);
                await SendDataToAzureIoTHub(JsonData);
            }
            catch (Exception ex)
            {
                MessageDialog dialog = new MessageDialog("Error sending to IoTHub: " + ex.Message);
                await dialog.ShowAsync();
            }
        }

        private async Task SendDataToAzureIoTHub(string text)
        {
            try
            {
                DeviceClient deviceClient = DeviceClient.CreateFromConnectionString(connectionstring, TransportType.Http1);

                //  var text = "Hellow, Windows 10!";
                var msg = new Message(Encoding.UTF8.GetBytes(text));

                await deviceClient.SendEventAsync(msg);
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
            }
        }

        public T Deserialize<T>(string json)
        {
            var _Bytes = Encoding.Unicode.GetBytes(json);
            using (MemoryStream _Stream = new MemoryStream(_Bytes))
            {
                var _Serializer = new DataContractJsonSerializer(typeof(T));
                return (T)_Serializer.ReadObject(_Stream);
            }
        }

        public string Serialize(object instance)
        {
            try
            {
                using (MemoryStream _Stream = new MemoryStream())
                {
                    var _Serializer = new DataContractJsonSerializer(instance.GetType());
                    _Serializer.WriteObject(_Stream, instance);
                    _Stream.Position = 0;
                    using (StreamReader _Reader = new StreamReader(_Stream))
                    { return _Reader.ReadToEnd(); }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.ToString());
                return null;
            }
        }

        private void AzureStorageButton_Click(object sender, RoutedEventArgs e)
        {
            timerDataTransfer = ThreadPoolTimer.CreatePeriodicTimer(dataTransmitterTick, TimeSpan.FromMilliseconds(Convert.ToInt32(5000)));
        }

        private async void dataTransmitterTick(ThreadPoolTimer timer)
        {
            try
            {
                CloudStorageAccount storageAccount = CloudStorageAccount.Parse("DefaultEndpointsProtocol=https;AccountName=<your account name>;AccountKey=<your account key>");

                // Create the table client.
                CloudTableClient tableClient = storageAccount.CreateCloudTableClient();

                // Create the CloudTable object that represents the " AccelerometerTable " table.
                CloudTable table = tableClient.GetTableReference("LattePandaTable");
                await table.CreateIfNotExistsAsync();

                // Create a new customer entity.
                SensorEntity ent = new SensorEntity();
                ent.MeasurementTime = DateTime.Now;
                ent.Node = nodeValue;
                ent.Gas = gasValue;
                ent.Light = lightValue;
                ent.HumanDetection = humanDetectionValue;
                ent.Temperature = temperatureValue;

                // Create the TableOperation that inserts the customer entity.
                TableOperation insertOperation = TableOperation.Insert(ent);
                // Execute the insert operation.
                await table.ExecuteAsync(insertOperation);
            }
            catch (Exception ex)
            {
                MessageDialog dialog = new MessageDialog("Error sending to Azure: " + ex.Message);
                await dialog.ShowAsync();
            }
        }
    }
}
