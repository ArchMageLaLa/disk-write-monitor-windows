using LibreHardwareMonitor.Hardware;

// 只开存储，避免加载 CPU/主板传感器所需的 PawnIO/WinRing0 内核驱动
var computer = new Computer { IsStorageEnabled = true };
computer.Open();

Console.WriteLine("=== Hardware list ===");
foreach (var hw in computer.Hardware)
{
    hw.Update();
    Console.WriteLine($"[{hw.HardwareType}] {hw.Name}");
    Console.WriteLine($"    identifier: {hw.Identifier}");

    foreach (var sensor in hw.Sensors)
        Console.WriteLine($"    {sensor.SensorType,-14} {sensor.Name,-30} {sensor.Value}");

    foreach (var sub in hw.SubHardware)
    {
        sub.Update();
        Console.WriteLine($"    [sub:{sub.HardwareType}] {sub.Name}");
        foreach (var sensor in sub.Sensors)
            Console.WriteLine($"        {sensor.SensorType,-14} {sensor.Name,-26} {sensor.Value}");
    }
}

Console.WriteLine();
Console.WriteLine($"storage hardware count = {computer.Hardware.Count(h => h.HardwareType == HardwareType.Storage)}");

computer.Close();
