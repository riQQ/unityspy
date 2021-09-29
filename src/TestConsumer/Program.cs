using HackF5.UnitySpy;
using HackF5.UnitySpy.Detail;
using System;
using System.Diagnostics;
using System.Linq;

namespace TestConsumer
{
	class Program
	{
		static int Main(string[] args)
        {
            var mtgaProcess = Process.GetProcessesByName("MTGA").FirstOrDefault();
            if (mtgaProcess == null)
            {
                Console.WriteLine("MTGA process not found");
                return 1;
            }
            IAssemblyImage assemblyImage = AssemblyImageFactory.Create(mtgaProcess.Id);
            //var x = assemblyImage.GetTypeDefinition("WrapperController").GetStaticValue<object>("<Instance>k__BackingField");
            var cardsDictionary = assemblyImage["WrapperController"]["<Instance>k__BackingField"]["<InventoryManager>k__BackingField"]["_inventoryServiceWrapper"]["<Cards>k__BackingField"];
            var mgdClass = (ManagedClassInstance)cardsDictionary;
            var dict = (object[]) cardsDictionary["entries"];
            var firstEntry = (ManagedStructInstance)dict.FirstOrDefault();

            foreach (var entry in dict.OfType<ManagedStructInstance>())
            {
                entry.Print();
            }

            return 0;
        }
	}
    public static class Extensions
    {
        public static void Print(this ManagedStructInstance instance)
        {
            var data = instance.GetData(4 * 4);
            var cardId = BitConverter.ToInt32(data, 0);
            var amount = BitConverter.ToInt32(data, 12);
            Console.WriteLine($"Id {cardId} - copies {amount} [{instance.GetAddress()}]");
        }
    }
}
