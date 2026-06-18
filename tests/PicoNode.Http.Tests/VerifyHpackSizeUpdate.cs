/// <summary>
/// Standalone TDD verification for HPACK Dynamic Table Size Update fix.
/// Run: dotnet run --project tests/PicoNode.Http.Tests -c Debug
/// Expected RED output: "FAIL: Capacity was 4096, expected 2048"
/// Expected GREEN output: "PASS: All HPACK size update tests passed"
/// </summary>
internal static class VerifyHpackSizeUpdate
{
    public static bool Run()
    {
        var allPassed = true;

        // Test 1: Size update to 2048
        {
            var block = new byte[] { 0x3F, 0xE1, 0x0F }; // value = 2048
            var table = new HpackDynamicTable();
            var success = HpackDecoder.TryDecode(block, out var headers, table);

            if (!success || headers.Count != 0 || table.Capacity != 2048)
            {
                Console.WriteLine($"FAIL Test1: Capacity={table.Capacity}, expected=2048");
                allPassed = false;
            }
        }

        // Test 2: Size update to 4096 (same as default, no-op)
        {
            var block = new byte[] { 0x3F, 0xA1, 0x1F }; // value = 4096
            var table = new HpackDynamicTable();
            var success = HpackDecoder.TryDecode(block, out var headers, table);

            if (!success || table.Capacity != 4096)
            {
                Console.WriteLine($"FAIL Test2: Capacity={table.Capacity}, expected=4096");
                allPassed = false;
            }
        }

        // Test 3: Size update to 0 (should reset to DefaultCapacity = 4096)
        {
            var block = new byte[] { 0x20 }; // value = 0
            var table = new HpackDynamicTable(8192);
            var success = HpackDecoder.TryDecode(block, out var headers, table);

            if (!success || table.Capacity != 4096)
            {
                Console.WriteLine($"FAIL Test3: Capacity={table.Capacity}, expected=4096");
                allPassed = false;
            }
        }

        // Test 4: Size update after header entry
        {
            var addBlock = new byte[]
            {
                0x40,
                0x03,
                0x6B,
                0x65,
                0x79, // "key"
                0x03,
                0x76,
                0x61,
                0x6C, // "val"
            };
            var sizeBlock = new byte[] { 0x3F, 0xE1, 0x07 }; // value = 1024
            var combined = new byte[addBlock.Length + sizeBlock.Length];
            addBlock.CopyTo(combined, 0);
            sizeBlock.CopyTo(combined, addBlock.Length);

            var table = new HpackDynamicTable();
            var success = HpackDecoder.TryDecode(combined, out var headers, table);

            if (!success || headers.Count != 1 || table.Capacity != 1024)
            {
                Console.WriteLine($"FAIL Test4: Capacity={table.Capacity}, expected=1024");
                allPassed = false;
            }
        }

        if (allPassed)
            Console.WriteLine("PASS: All HPACK size update tests passed");
        return allPassed;
    }
}
