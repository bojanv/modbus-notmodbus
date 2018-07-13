using System;
using System.Text;
using Microsoft.Azure.Devices.Client;
using Microsoft.Azure.Devices.Shared;
using Newtonsoft.Json;
using ModbusTcp;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Microsoft.Extensions.Configuration;
using System.IO;
using System.Threading;
using System.Reflection;

namespace modbus_notmodbus
{
    class Program
    {
        static IConfiguration config = LoadConfig();

        public static uint pollingInterval = 11; // seconds
        public static string deviceId = "ModbusCollector";
        public static string modbusHost = config.GetConnectionString("modbusHost");
        public static int modbusPort = Convert.ToInt16(config.GetConnectionString("modbusPort"));
        public static string deviceConnStr = config.GetConnectionString("deviceConnStr");
        static DeviceClient c;

        static async Task Main(string[] args)
        {
            //#if !DEBUG
            AppDomain.CurrentDomain.UnhandledException += CallBombSquad;
            //#endif
            // try
            // {
            //     m.Init();
            // }
            // catch (Exception ex)
            // {
            //     Console.WriteLine($"[EXCEPTION] Exception while instantiating Modbus client: {ex.Message}");
            //     //#if !DEBUG
            //     //Environment.Exit(-1);
            //     //#endif
            // }

            c = DeviceClient.CreateFromConnectionString(deviceConnStr);
            Twin twin = await c.GetTwinAsync();
            if (twin.Properties.Desired["pollingInterval"] != Program.pollingInterval)
            {
                Console.WriteLine("[DEBUG] Setting new pollingInterval: " +
                    $"{twin.Properties.Desired["pollingInterval"]} seconds");
                try
                {
                    Program.pollingInterval = twin.Properties.Desired["pollingInterval"];
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EXCEPTION] Unable to set pollingInterval: {ex.Message}");
                }
            }
            await c.SetDesiredPropertyUpdateCallbackAsync(OnDesiredPropertyChanged, null);

            while (true)
            {
                object telemetryObject = GetModbusData()?.Result;
                while (telemetryObject == null) {
                    telemetryObject = GetModbusData()?.Result;
                }

                Console.WriteLine("[DEBUG] Serialized telemetry object:\n" +
                    JsonConvert.SerializeObject(telemetryObject, Formatting.Indented));

                byte[] payload = Encoding.ASCII.GetBytes(JsonConvert.SerializeObject(telemetryObject));
                Message message = new Message(payload);

                Console.WriteLine("Sending message to Azure IoT Hub...");
                await c.SendEventAsync(message);
                Console.WriteLine("OK");

                await Spinner.SleepSpinner(pollingInterval);
            }
        }

        private static void CallBombSquad(object sender, UnhandledExceptionEventArgs e)
        {
            Console.WriteLine($"[BombSquad][EXCEPTION] {e.ToString()}");
        }

        static IConfiguration LoadConfig()
        {
            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables();

            IConfiguration Configuration = builder.Build();

            return Configuration;
        }

        static Task<object> GetModbusData()
        {
            Console.WriteLine("Fetching MODBUS data...");
            ModbusClient m = new ModbusClient(modbusHost, modbusPort);
            bool modbusClientAlive = false;
            while (!modbusClientAlive)
            {
                try
                {
                    m.Init();
                    modbusClientAlive = true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EXCEPTION] Exception while instantiating Modbus client: {ex.Message}");
                    Console.WriteLine("\nSleeping for 15 seconds before retrying to talk to Modbus host...\n");
                    Task.Delay(TimeSpan.FromSeconds(15)).Wait();
                }
            }
            CancellationToken ct = new CancellationToken();
            Task<object> t = Task.Run<object>(async () =>
            {
                short[] voltage = Array.Empty<short>();
                short[] current = Array.Empty<short>();
                string hardwareId = String.Empty;
                try
                {
                    voltage = await m.ReadRegistersAsync(40001, 3);
                    current = await m.ReadRegistersAsync(41001, 3);
                    hardwareId = "Function Code 0x2b (43)";
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EXCEPTION] Exception while calling ReadRegistersAsync(): {ex.Message}");
                    Console.WriteLine("\nSleeping for 5 seconds before retrying...\n");
                    Task.Delay(TimeSpan.FromSeconds(5)).Wait();
                }
                
                return new {
                    deviceId = deviceId,
                    voltage = voltage,
                    current = current,
                    hardwareId = hardwareId
                };
            }, ct);

            if (t.Wait(9000, ct))
            {
                return t;
            }
            else
            {
                Console.WriteLine("Aborting modbus Task, took too long to return.");
                //Console.WriteLine("Restarting collector...\n");
                //Assembly a = Assembly.GetExecutingAssembly();
                //System.Diagnostics.Process.Start("dotnet.exe", a.Location);
                //Environment.Exit(-2);
                return null;
            }
        }

        static async Task OnDesiredPropertyChanged(TwinCollection desiredProperties, object userContext)
        {
            Console.WriteLine();
            foreach (var prop in desiredProperties)
            {
                var pair = (KeyValuePair<string, object>)prop;
                var value = pair.Value as JValue;
                Console.WriteLine($"[DEBUG] desiredProp: {pair.Key} = {pair.Value.ToString()}");
            }

            if (desiredProperties["pollingInterval"] != Program.pollingInterval)
            {
                Console.WriteLine($"[DEBUG] Setting new pollingInterval: {desiredProperties["pollingInterval"]}");
                try
                {
                    Program.pollingInterval = desiredProperties["pollingInterval"];
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[EXCEPTION] Unable to set pollingInterval: {ex.Message}");
                }
            }

            TwinCollection reportedProperties = new TwinCollection();
            reportedProperties["pollingInterval"] = Program.pollingInterval;
            await c.UpdateReportedPropertiesAsync(reportedProperties);
        }
    }
}