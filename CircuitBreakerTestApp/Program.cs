using CircuiteBreaker;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace CircuitBreakerTestApp
{
    class Program
    {
        private static long DoThingPrimeNumber = 100000;
        private static TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
        private static string WorkingDirectory = @"C:\Cache";

        static void Main(string[] args)
        {
            RunSampleCircuitBreaker();
        }

        static void RunSampleCircuitBreaker(bool throwError = false)
        {
            string cacheKey = string.Format("DoThing-FindPrimeNumber-{0}", DoThingPrimeNumber);

            var breaker = new CircuitBreaker(
                    circuitId: cacheKey,
                    cacheKey: cacheKey,
                    workingDirectory: WorkingDirectory,
                    cacheDuration: CacheDuration
                );

            bool inCache = true;

            dynamic result = breaker.Execute(() => {
                inCache = false;
                var _result = DoThing(throwError);
                return _result;
            });

            if(inCache)
            {
                Console.WriteLine("Result was in cache, returned from cache.");
            } else
            {
                Console.WriteLine("Result was NOT in cache and function was run.");
            }

            OutputThing(result);
        }

        static dynamic DoThing(bool throwError)
        {
            Console.WriteLine(string.Format("Finding PrimeNumber for {0}", DoThingPrimeNumber));

            if(throwError)
            {
                throw new ApplicationException("An error was raised while doing the thing.");
            }

            int count = 0;
            long a = 2;
            while (count < DoThingPrimeNumber)
            {
                long b = 2;
                int prime = 1;// to check if found a prime
                while (b * b <= a)
                {
                    if (a % b == 0)
                    {
                        prime = 0;
                        break;
                    }
                    b++;
                }
                if (prime > 0)
                {
                    count++;
                }
                a++;
            }

            dynamic result = new { PrimeNumber = a-- };

            return result;

        }

        static void OutputThing(dynamic result)
        {
            
            Console.WriteLine(string.Format("PrimeNumber is {0}", result.PrimeNumber));
            

            Console.WriteLine("Enter a 'continue' to go again and try to read from cache, 'error' to go again and throw an error and see how it responds, 'exit' to exit.");
            string input = Console.ReadLine();

            switch(input)
            {
                case "continue":
                    RunSampleCircuitBreaker();
                    break;
                case "error":
                    RunSampleCircuitBreaker(true);
                    break;
                case "exit":
                    
                    break;
                default:
                    break;
            }

        }
    }
}
