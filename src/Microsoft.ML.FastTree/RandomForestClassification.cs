// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Float = System.Single;

using System;
using System.Linq;
using Microsoft.ML.Runtime;
using Microsoft.ML.Runtime.CommandLine;
using Microsoft.ML.Runtime.Data;
using Microsoft.ML.Runtime.EntryPoints;
using Microsoft.ML.Runtime.FastTree;
using Microsoft.ML.Runtime.FastTree.Internal;
using Microsoft.ML.Runtime.Internal.Calibration;
using Microsoft.ML.Runtime.Internal.Internallearn;
using Microsoft.ML.Runtime.Model;
using Microsoft.ML.Runtime.Training;

[assembly: LoadableClass(FastForestClassification.Summary, typeof(FastForestClassification), typeof(FastForestClassification.Arguments),
    new[] { typeof(SignatureBinaryClassifierTrainer), typeof(SignatureTrainer), typeof(SignatureTreeEnsembleTrainer), typeof(SignatureFeatureScorerTrainer) },
    FastForestClassification.UserNameValue,
    FastForestClassification.LoadNameValue,
    "FastForest",
    FastForestClassification.ShortName,
    "ffc")]

[assembly: LoadableClass(typeof(IPredictorProducing<Float>), typeof(FastForestClassificationPredictor), null, typeof(SignatureLoadModel),
    "FastForest Binary Executor",
    FastForestClassificationPredictor.LoaderSignature)]

[assembly: LoadableClass(typeof(void), typeof(FastForest), null, typeof(SignatureEntryPointModule), "FastForest")]

namespace Microsoft.ML.Runtime.FastTree
{
    public abstract class FastForestArgumentsBase : TreeArgs
    {
        [Argument(ArgumentType.AtMostOnce, HelpText = "Number of labels to be sampled from each leaf to make the distribtuion", ShortName = "qsc")]
        public int QuantileSampleCount = 100;

        public FastForestArgumentsBase()
        {
            FeatureFraction = 0.7;
            BaggingSize = 1;
            SplitFraction = 0.7;
        }
    }

    public sealed class FastForestClassificationPredictor :
        FastTreePredictionWrapper
    {
        public const string LoaderSignature = "FastForestBinaryExec";
        public const string RegistrationName = "FastForestClassificationPredictor";

        private static VersionInfo GetVersionInfo()
        {
            return new VersionInfo(
                modelSignature: "FFORE BC",
                // verWrittenCur: 0x00010001, Initial
                // verWrittenCur: 0x00010002, // InstanceWeights are part of QuantileRegression Tree to support weighted intances
                // verWrittenCur: 0x00010003, // _numFeatures serialized
                // verWrittenCur: 0x00010004, // Ini content out of predictor
                // verWrittenCur: 0x00010005, // Add _defaultValueForMissing
                verWrittenCur: 0x00010006, // Categorical splits.
                verReadableCur: 0x00010005,
                verWeCanReadBack: 0x00010001,
                loaderSignature: LoaderSignature);
        }

        protected override uint VerNumFeaturesSerialized { get { return 0x00010003; } }

        protected override uint VerDefaultValueSerialized { get { return 0x00010005; } }

        protected override uint VerCategoricalSplitSerialized { get { return 0x00010006; } }

        public override PredictionKind PredictionKind { get { return PredictionKind.BinaryClassification; } }

        internal FastForestClassificationPredictor(IHostEnvironment env, Ensemble trainedEnsemble, int featureCount,
            string innerArgs)
            : base(env, RegistrationName, trainedEnsemble, featureCount, innerArgs)
        {
        }

        private FastForestClassificationPredictor(IHostEnvironment env, ModelLoadContext ctx)
            : base(env, RegistrationName, ctx, GetVersionInfo())
        {
        }

        protected override void SaveCore(ModelSaveContext ctx)
        {
            base.SaveCore(ctx);
            ctx.SetVersionInfo(GetVersionInfo());
        }

        public static IPredictorProducing<Float> Create(IHostEnvironment env, ModelLoadContext ctx)
        {
            Contracts.CheckValue(env, nameof(env));
            env.CheckValue(ctx, nameof(ctx));
            ctx.CheckAtModel(GetVersionInfo());
            var predictor = new FastForestClassificationPredictor(env, ctx);
            ICalibrator calibrator;
            ctx.LoadModelOrNull<ICalibrator, SignatureLoadModel>(env, out calibrator, @"Calibrator");
            if (calibrator == null)
                return predictor;
            return new SchemaBindableCalibratedPredictor(env, predictor, calibrator);
        }
    }

    public sealed partial class FastForestClassification :
        RandomForestTrainerBase<FastForestClassification.Arguments, IPredictorWithFeatureWeights<Float>>
    {
        public sealed class Arguments : FastForestArgumentsBase
        {
            [Argument(ArgumentType.AtMostOnce, HelpText = "Upper bound on absolute value of single tree output", ShortName = "mo")]
            public Double MaxTreeOutput = 100;

            [Argument(ArgumentType.AtMostOnce, HelpText = "The calibrator kind to apply to the predictor. Specify null for no calibration", Visibility = ArgumentAttribute.VisibilityType.EntryPointsOnly)]
            public ICalibratorTrainerFactory Calibrator = new PlattCalibratorTrainerFactory();

            [Argument(ArgumentType.AtMostOnce, HelpText = "The maximum number of examples to use when training the calibrator", Visibility = ArgumentAttribute.VisibilityType.EntryPointsOnly)]
            public int MaxCalibrationExamples = 1000000;
        }

        internal const string LoadNameValue = "FastForestClassification";
        public const string UserNameValue = "Fast Forest Classification";
        public const string Summary = "Uses a random forest learner to perform binary classification.";
        public const string ShortName = "ff";

        private bool[] _trainSetLabels;

        public FastForestClassification(IHostEnvironment env, Arguments args)
            : base(env, args)
        {
        }

        public override bool NeedCalibration
        {
            get { return true; }
        }

        public override PredictionKind PredictionKind { get { return PredictionKind.BinaryClassification; } }

        public override void Train(RoleMappedData trainData)
        {
            using (var ch = Host.Start("Training"))
            {
                ch.CheckValue(trainData, nameof(trainData));
                trainData.CheckBinaryLabel();
                trainData.CheckFeatureFloatVector();
                trainData.CheckOptFloatWeight();
                FeatureCount = trainData.Schema.Feature.Type.ValueCount;
                ConvertData(trainData);
                TrainCore(ch);
                ch.Done();
            }
        }

        public override IPredictorWithFeatureWeights<Float> CreatePredictor()
        {
            Host.Check(TrainedEnsemble != null,
                "The predictor cannot be created before training is complete");

            // LogitBoost is naturally calibrated to
            // output probabilities when transformed using
            // the logistic function, so if we have trained no 
            // calibrator, transform the scores using that.

            // REVIEW: Need a way to signal the outside world that we prefer simple sigmoid?
            return new FastForestClassificationPredictor(Host, TrainedEnsemble, FeatureCount, InnerArgs);
        }

        protected override ObjectiveFunctionBase ConstructObjFunc(IChannel ch)
        {
            return new ObjectiveFunctionImpl(TrainSet, _trainSetLabels, Args);
        }

        protected override void PrepareLabels(IChannel ch)
        {
            // REVIEW: Historically FastTree has this test as >= 1. TLC however
            // generally uses > 0. Consider changing FastTree to be consistent.
            _trainSetLabels = TrainSet.Ratings.Select(x => x >= 1).ToArray(TrainSet.NumDocs);
        }

        protected override Test ConstructTestForTrainingData()
        {
            return new BinaryClassificationTest(ConstructScoreTracker(TrainSet), _trainSetLabels, 1);
        }

        private sealed class ObjectiveFunctionImpl : RandomForestObjectiveFunction
        {
            private readonly bool[] _labels;

            public ObjectiveFunctionImpl(Dataset trainSet, bool[] trainSetLabels, Arguments args)
                : base(trainSet, args, args.MaxTreeOutput)
            {
                _labels = trainSetLabels;
            }

            protected override void GetGradientInOneQuery(int query, int threadIndex)
            {
                int begin = Dataset.Boundaries[query];
                int end = Dataset.Boundaries[query + 1];
                for (int i = begin; i < end; ++i)
                    Gradient[i] = _labels[i] ? 1 : -1;
            }
        }
    }

    public static partial class FastForest
    {
        [TlcModule.EntryPoint(Name = "Trainers.FastForestBinaryClassifier", 
            Desc = FastForestClassification.Summary, 
            Remarks = FastForestClassification.Remarks, 
            UserName = FastForestClassification.UserNameValue, 
            ShortName = FastForestClassification.ShortName)]
        public static CommonOutputs.BinaryClassificationOutput TrainBinary(IHostEnvironment env, FastForestClassification.Arguments input)
        {
            Contracts.CheckValue(env, nameof(env));
            var host = env.Register("TrainFastForest");
            host.CheckValue(input, nameof(input));
            EntryPointUtils.CheckInputArgs(host, input);

            return LearnerEntryPointsUtils.Train<FastForestClassification.Arguments, CommonOutputs.BinaryClassificationOutput>(host, input,
                () => new FastForestClassification(host, input),
                () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.LabelColumn),
                () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.WeightColumn),
                () => LearnerEntryPointsUtils.FindColumn(host, input.TrainingData.Schema, input.GroupIdColumn),
                calibrator: input.Calibrator, maxCalibrationExamples: input.MaxCalibrationExamples);

        }
    }
}
