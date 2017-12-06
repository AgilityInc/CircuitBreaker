# Circuit Breaker
Class library to manage caching for CPU intensive workloads or accessing volatile resources with memory and file based caching.

## Use Cases
1. Caching responses from volatile resources such as APIs
2. Caching responses from functions/methods that are CPU intensive

## Features
1. Caching in memory
2. File backed caching. This will serialize the result of the function to JSON and save to file. It will be used if cache is kicked out of memory pre-maturely. 
3. Dynamic Open/Close of the circuit.
4. Handle errors gracefully by falling back to the last result from file-backed cache

## Usage
Intialize the Circuite Breaker class with the following required attributes. CircuitId could represent a set of calls to an API while the CacheKey could be a specific API call varied by parameters.
```
var breaker = new CircuitBreaker(
                    circuitId: "My-circuit-key",
                    cacheKey: "My-unique-key",
                    workingDirectory: @"C:\MyCacheDirectory",
                    cacheDuration: Timespan.FromSeconds(60)
                );
```

Execute your function by specifying it as a function to invoke if the response is not in Cache.
```
dynamic result = breaker.Execute(() => {
                var _result = DoThing();
                return _result;
            });
```

## Testing
This repo has a console app you can run to test the functionality. This console app will run a CPU intensive function of finding a prime number and subsequently cache it for 30 seconds in memory and create a corresponding JSON file of the result. Once the function runs, you have an opportunity to run it again from the same command prompt and note how it retrieves the result from memory cache. You can then stop the program, and start it again. This will kick the result out of memory cache, but still retrieve the last result from file (if still within the cache duration).

1. Open .sln CircuitBreakerTestApp.sln
2. Run the Console Application
3. Set the "DoPrimeNumber" value to something high to use more CPU
4. Set the "WorkingDirectory" which represents where your file based cache will be stored
5. Run the program
6. Optionally, "continue" the program to see how it will fetch from cache/file based on your "CacheDuration"
7. Optionally, enter "error" within the program to run the next iteration of the function to throw an error and note how it will fallback to the latest file based cache.


