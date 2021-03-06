﻿using AutoMapper;
using System;
using System.Diagnostics.Contracts;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace WeETL.Core
{
    public abstract class ETLComponent<TInputSchema, TOutputSchema> : ETLCoreComponent
       // where TInputSchema : class//, new()
       // where TOutputSchema : class, new()
    {
        #region private vars

        private int _outputCounter;
        private IDisposable _inputDisposable;
        private Action<TOutputSchema> _transform;
        private IMapper _mapper;
        private Action<IMappingExpression<TInputSchema, TOutputSchema>> _mappercfg;

        private readonly ISubject<ETLComponent<TInputSchema, TOutputSchema>> _onSetInput = new Subject<ETLComponent<TInputSchema, TOutputSchema>>();
        private readonly ISubject<ETLComponent<TInputSchema, TOutputSchema>> _onRemoveInput = new Subject<ETLComponent<TInputSchema, TOutputSchema>>();
        private readonly ISubject<TInputSchema> _inputSubject = new Subject<TInputSchema>();
        private readonly ISubject<TOutputSchema> _outputSubject = new Subject<TOutputSchema>();

        private readonly ISubject<(int, TInputSchema)> _onBeforeBeforeTransform = new Subject<(int, TInputSchema)>();
        private readonly ISubject<(int, TInputSchema)> _onAfterBeforeTransform = new Subject<(int, TInputSchema)>();
        private readonly ISubject<(int, TInputSchema)> _onBeforeAfterTransform = new Subject<(int, TInputSchema)>();
        private readonly ISubject<(int, TInputSchema)> _onAfterAfterTransform = new Subject<(int, TInputSchema)>();
        #endregion
        #region ctor
        public ETLComponent() : base()
        {

        }
        #endregion
        protected bool DeferSendingOutput { get; set; }

        protected ISubject<TInputSchema> InputHandler => _inputSubject;
        protected ISubject<TOutputSchema> OutputHandler => _outputSubject;


        protected ISubject<(int, TInputSchema)> BeforeBeforeTransformHandler => _onBeforeBeforeTransform;
        protected ISubject<(int, TInputSchema)> AfterBeforeTransformHandler => _onAfterBeforeTransform;
        protected ISubject<(int, TInputSchema)> BeforeAfterTransformHandler => _onBeforeAfterTransform;
        protected ISubject<(int, TInputSchema)> AfterAfterTransformHandler => _onAfterAfterTransform;
        //public IObservable<(int, TInputSchema)> OnBeforeTransform => BeforeTransformHandler.AsObservable();
        protected IMapper Mapper
        {
            get
            {
                if (_mapper == null)
                    CreateMapper();
                return _mapper;
            }
            set
            {
                //Enable override Mapper
                _mapper = value;
            }
        }
        #region public methods
        public virtual bool AddInput(Job job, IObservable<TInputSchema> obs)
        {


            var result = SetObservable(
                obs,
                AcceptInput,
                $"{this.GetType().Name} is Startable. It does not accept input",
                ref _inputDisposable,
                InternalOnException,
                InternalOnInputCompleted,
                InternalOnInputBeforeTransform,
                InternalInputTransform,
                InternalOnInputAfterTransform,
                InternalSendOutput,
                DeferSendingOutput
                );
            if (result)
            {
                this.Job = job;
                job.AddComponent(this);
                _onSetInput.OnNext(this);
            }
            return result;

        }
        protected bool SetObservable<TIn, TOut>(
            IObservable<TIn> obs,
            bool accept,
            string acceptErrorMessage,
            ref IDisposable disposable,
            Action<Exception> exception,
            Action completed)
             where TIn : class
            where TOut : class, new()
        {
            if (!accept)
            {
                ErrorHandler.OnNext(new ConnectorException(acceptErrorMessage));
                return false;
            }
            disposable = obs.TimeInterval().Subscribe(
               row =>
               {
                   if (!IsRunning)
                   {
                       StartHandler.OnNext((this, DateTime.Now));
                   }

               },
               exception,
               completed);
            return true;
        }
        protected bool SetObservable(
            IObservable<TInputSchema> obs,
            bool accept,
            string acceptErrorMessage,
            ref IDisposable disposable,
            Action<Exception> exception,
            Action completed,
            Action<int, TInputSchema> beforeTransform,
            Func<TInputSchema, TOutputSchema> transform,
            Action<int, TOutputSchema> afterTransform,
            Action<int, TOutputSchema> sendOutput,
            bool deferOutput = false)

        {
            if (!accept)
            {
                ErrorHandler.OnNext(new ConnectorException(acceptErrorMessage));
                return false;
            }
            //_input = component;
            disposable = obs.TimeInterval().Subscribe(
               row =>
               {
                   if (!IsRunning)
                   {
                       StartHandler.OnNext((this, DateTime.Now));
                   }
                   if ((Enabled || !Passthrue) && beforeTransform != null && transform != null && afterTransform != null && sendOutput != null)
                   {
                       try
                       {
                           _outputCounter++;
                           TInputSchema value = row.Value;

                           if (!Passthrue)
                           {
                               SendBeforeBeforeTransformEvent(_outputCounter, value);
                               beforeTransform(_outputCounter, row.Value);
                               SendAfterBeforeTransformEvent(_outputCounter, value);
                           }
                           TOutputSchema transformed = transform(row.Value);
                           if (!Passthrue)
                           {
                               SendBeforeAfterTransformEvent(_outputCounter, value);
                               afterTransform(_outputCounter, transformed);
                               SendAfterAfterTransformEvent(_outputCounter, value);
                           }

                           if (!deferOutput) sendOutput(_outputCounter, transformed);
                       }
                       catch (Exception ex)
                       {
                           ErrorHandler.OnNext(new ConnectorException($"An error happened on {this.Name}", ex));
                       }
                   }
               },
               exception,
               completed);
            //set.OnNext(this);
            return true;
        }
        /// <summary>
        /// Transform the output schema row/property by your one
        /// </summary>
        /// <param name="@action">The action that perform the transformation</param>
        public void Transform(Action<TOutputSchema> @action)
        {
            this._transform = action;
        }
        #endregion

        #region protected methods
        /*   public void SetMapperConfiguration(Action<IMapperConfigurationExpression> cfg)
           {
               this._mapperConfiguration = cfg;
           }*/
        public void MapperConfiguation(Action<IMappingExpression<TInputSchema, TOutputSchema>> fn)
        {
            this._mappercfg = fn;

        }
        protected virtual void CreateMapper()
        {
            var config = new MapperConfiguration(cfg =>
            {
                var s = cfg.CreateMap<TInputSchema, TOutputSchema>();
                _mappercfg?.Invoke(s);
            });
            //var config = _mapperConfiguration == null?new MapperConfiguration(cfg => cfg.CreateMap<TInputSchema, TOutputSchema>()):new MapperConfiguration(_mapperConfiguration);
            Mapper = config.CreateMapper();
        }
        protected virtual void InternalSendOutput(int index, TOutputSchema row)
        {
            Contract.Requires(index > 0, "InternalOnInputBeforeTransform require index >0");
            Contract.Requires(row != null, "InternalSendOutput row cannot be null");
            _outputSubject.OnNext(row);
        }
        protected virtual void SendBeforeBeforeTransformEvent(int index, TInputSchema row)
        {
            BeforeBeforeTransformHandler.OnNext((index, row));
        }
        protected virtual void SendAfterBeforeTransformEvent(int index, TInputSchema row)
        {
            AfterBeforeTransformHandler.OnNext((index, row));
        }
        protected virtual void InternalOnInputBeforeTransform(int index, TInputSchema row)
        {
            Contract.Requires(index > 0, "InternalOnInputBeforeTransform require index >0");
            Contract.Requires(row != null, "InternalOnInputBeforeTransform row cannot be null");
            //BeforeTransformHandler.OnNext((index, row));
        }
        protected virtual TOutputSchema InternalInputTransform(TInputSchema row)
        {
            Contract.Requires(Mapper != null, "You must create Automapper configuration before using Transforming Schema");
            Contract.Requires(row != null, "InternalInputTransform row cannot be null");
            Contract.Ensures(Contract.ValueAtReturn<TOutputSchema>(out var x) != null, "InternalInputTransform the return value cannot be null. Checkou the mapper configuration");
            var result = Mapper.Map<TOutputSchema>(row);
            this._transform?.Invoke(result);
            return result;

        }
        protected virtual void SendBeforeAfterTransformEvent(int index, TInputSchema row)
        {
            BeforeAfterTransformHandler.OnNext((index, row));
        }
        protected virtual void SendAfterAfterTransformEvent(int index, TInputSchema row)
        {
            AfterAfterTransformHandler.OnNext((index, row));
        }
        protected virtual void InternalOnInputAfterTransform(int index, TOutputSchema row)
        {
            Contract.Requires(index > 0, "InternalOnInputAfterTransform require index >0");
            Contract.Requires(row != null, "InternalOnInputAfterTransform row cannot be null");

        }

        protected virtual void InternalOnException(Exception e)
        {
            Contract.Requires(e != null, "InternalOnException exception cannot be null");

        }

        protected virtual void InternalOnInputCompleted()
        {
            _outputSubject.OnCompleted();
            CompletedHandler.OnNext((this, DateTime.Now));

        }


        #endregion

        #region public properties
        public bool AcceptInput => (_inputDisposable == null && !(this is IStartable));
        //public IObservable<ConnectorException> OnError => _onError.AsObservable();
        public IObservable<ETLComponent<TInputSchema, TOutputSchema>> OnSetInput => _onSetInput.AsObservable();
        public IObservable<ETLComponent<TInputSchema, TOutputSchema>> OnRemoveInput => _onRemoveInput.AsObservable();
        public IObservable<TOutputSchema> OnOutput => OutputHandler.AsObservable();

        public Job Job { get; private set; }
        #endregion


        #region IDisposable
        protected override void InternalDispose()
        {
            base.InternalDispose();
            _inputDisposable?.Dispose();
        }

        #endregion
    }

    public class PopulateAction<TComponent, T> : ActionRule<Func<TComponent, T, object>>
    {
    }
    public class ActionRule<T>
    {
        /// <summary>
        /// Populate action
        /// </summary>
        public T Action { get; set; }

        /// <summary>
        /// Property name, maybe null for finalize or create.
        /// </summary>
        public string PropertyName { get; set; }



        /// <summary>
        /// Prohibits the rule from being applied in strict mode.
        /// </summary>
        public bool ProhibitInStrictMode { get; set; } = false;
    }

    public class PopulateOrdering<TComponent>
         where TComponent : class, new()
    {
        public Func<TComponent, object> Order { get; internal set; }
        public SortOrder SortOrder { get; internal set; }
    }

    public class OrderingRule<T>
    {

    }
}
