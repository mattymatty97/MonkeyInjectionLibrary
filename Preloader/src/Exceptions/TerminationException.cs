using System;

namespace InjectionLibrary.Exceptions;

public class TerminationException(string message) : Exception(message)
{
    
}