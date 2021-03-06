﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GitHub.Internals
{
    /// <summary>
    /// An instance of an experiment. This actually runs the control and the candidate and measures the result.
    /// </summary>
    /// <typeparam name="T">The return type of the experiment</typeparam>
    internal class ExperimentInstance<T>
    {
        static Random _random = new Random(DateTimeOffset.UtcNow.Millisecond);

        readonly Func<Task<T>> _control;
        readonly Func<Task<T>> _candidate;
        private readonly Func<T, T, bool> _resultComparison;

        private readonly IEqualityComparer<T> _resultEqualityComparer;
        readonly string _name;

        public ExperimentInstance(string name, Func<T> control, Func<T> candidate)
        {
            _name = name;
            _control = () => Task.FromResult(control());
            _candidate = () => Task.FromResult(candidate());
        }

        public ExperimentInstance(string name, Func<Task<T>> control, Func<Task<T>> candidate)
        {
            _name = name;
            _control = control;
            _candidate = candidate;
        }

        public ExperimentInstance(string name, Func<Task<T>> control, Func<Task<T>> candidate, Func<T, T, bool> resultComparison)
        {
            _name = name;
            _control = control;
            _candidate = candidate;
            _resultComparison = resultComparison;
        }

        public ExperimentInstance(string name, Func<T> control, Func<T> candidate, IEqualityComparer<T> resultEqualityComparer = null)
        {
            _name = name;
            _resultEqualityComparer = resultEqualityComparer;
            _control = () => Task.FromResult(control());
            _candidate = () => Task.FromResult(candidate());
        }

        public ExperimentInstance(string name, Func<Task<T>> control, Func<Task<T>> candidate, IEqualityComparer<T> resultEqualityComparer = null)
        {
            _name = name;
            _control = control;
            _candidate = candidate;
            _resultEqualityComparer = resultEqualityComparer;
        }

        public async Task<T> Run()
        {
            // Randomize ordering...
            var runControlFirst = _random.Next(0, 2) == 0;
            ExperimentResult controlResult;
            ExperimentResult candidateResult;

            if (runControlFirst)
            {
                controlResult = await Run(_control);
                candidateResult = await Run(_candidate);
            }
            else
            {
                candidateResult = await Run(_candidate);
                controlResult = await Run(_control);
            }

            // TODO: We need to compare that thrown exceptions are equivalent too https://github.com/github/scientist/blob/master/lib/scientist/observation.rb#L76
            // TODO: We're going to have to be a bit more sophisticated about this.
            bool success = CompareResults(controlResult, candidateResult);

            // TODO: Get that duration!
            var observation = new Observation(_name, success, controlResult.Duration, candidateResult.Duration);

            // TODO: Make this Fire and forget so we don't have to wait for this
            // to complete before we return a result
            await Scientist.ObservationPublisher.Publish(observation);

            if (controlResult.ThrownException != null) throw controlResult.ThrownException;
            return controlResult.Result;
        }

        //TODO: refactor this equality logic in to a better pattern and its own class
        /// <summary>
        /// Checks if two ExperimentResults are equal
        /// </summary>
        /// <param name="controlResult">Control ExperimentResult</param>
        /// <param name="candidateResult">Candidate ExperimentResult</param>
        /// <returns>
        ///  Returns true if: 
        /// 
        ///  The values of the observations are equal (using .Equals()) 
        ///  The values of the observations are equal according to Ts IEquatable&lt;T&gt; implementation, if implemented
        ///  The values of the observations are equal according to a comparison function, if given
        ///  The values of the observations are equal according to an IEqualityComparer&lt;T&gt; expression, if given  
        ///  Both observations raised an exception with the same Type and message.
        ///  Both values of the observation are null
        ///  
        ///  Returns false otherwise. 
        /// </returns>
        private bool CompareResults(ExperimentResult controlResult, ExperimentResult candidateResult)
        {
            if (_resultComparison != null)
            {
                return _resultComparison(controlResult.Result, candidateResult.Result);
            }

            if (_resultEqualityComparer != null)
            {
                return _resultEqualityComparer.Equals(controlResult.Result, candidateResult.Result);
            }

            var equatableResult = controlResult.Result as IEquatable<T>;
            if (equatableResult != null)
            {
                return equatableResult.Equals(candidateResult.Result);
            }


            bool success =
                  BothResultsAreNull(controlResult.Result, candidateResult.Result)
               || BothResultsEqual(controlResult.Result, candidateResult.Result)
               || ExceptionsAreEqual(controlResult.ThrownException, candidateResult.ThrownException);


            return success;
        }

      
        private bool BothResultsAreNull(T controlResult, T candidateResult)
        {
            return controlResult == null && candidateResult == null;
        }
        private bool BothResultsEqual(T controlResult, T candidateResult)
        {
            return controlResult != null && controlResult.Equals(candidateResult);
        }


        private bool ExceptionsAreEqual(Exception controlException, Exception candidateException)
        {
           //*Both observations raised an exception with the same class and message.
            bool bothExceptionsSameType = controlException != null && controlException.GetType().FullName.Equals(candidateException.GetType().FullName);
            bool bothExceptionsSameMessage = controlException != null && controlException.Message.Equals(candidateException.Message);

            return bothExceptionsSameType && bothExceptionsSameMessage;
        }

        static async Task<ExperimentResult> Run(Func<Task<T>> experimentCase)
        {
            try
            {
                // TODO: Refactor this into helper function?  
                var sw = new Stopwatch();
                sw.Start();
                var result = await experimentCase();
                sw.Stop();

                return new ExperimentResult(result, new TimeSpan(sw.ElapsedTicks));
            }
            catch (Exception e)
            {
                return new ExperimentResult(e, TimeSpan.Zero);
            }
        }

        class ExperimentResult
        {
            public ExperimentResult(T result, TimeSpan duration)
            {
                Result = result;
                Duration = duration;
            }

            public ExperimentResult(Exception exception, TimeSpan duration)
            {
                ThrownException = exception;
                Duration = duration;
            }

            public T Result { get; }

            public Exception ThrownException { get; }

            public TimeSpan Duration { get; }
        }
    }
}
