﻿using SharpLearning.Common.Interfaces;
using SharpLearning.Containers.Extensions;
using SharpLearning.Containers.Matrices;
using SharpLearning.GradientBoost.GBMDecisionTree;
using SharpLearning.GradientBoost.Loss;
using SharpLearning.GradientBoost.Models;
using System;
using System.Diagnostics;
using System.Linq;

namespace SharpLearning.GradientBoost.Learners
{
    /// <summary>
    /// <summary>
    /// Regression gradient boost learner based on 
    /// http://statweb.stanford.edu/~jhf/ftp/trebst.pdf
    /// A series of regression trees are fitted stage wise on the residuals of the previous stage.
    /// The resulting models are ensembled together using addition. Implementation based on:
    /// http://gradientboostedmodels.googlecode.com/files/report.pdf
    /// </summary>
    /// </summary>
    public class RegressionGradientBoostLearner : IIndexedLearner<double>, ILearner<double>
    {
        readonly GBMDecisionTreeLearner m_learner;
        readonly double m_learningRate;
        readonly int m_iterations;
        readonly double m_subSampleRatio;
        readonly Random m_random = new Random(42);
        readonly IGradientBoostLoss m_loss;

        /// <summary>
        /// <summary>
        ///  Base regression gradient boost learner. 
        ///  A series of regression trees are fitted stage wise on the residuals of the previous stage
        /// </summary>
        /// <param name="iterations">The number of iterations or stages</param>
        /// <param name="learningRate">How much each iteration should contribute with</param>
        /// <param name="maximumTreeDepth">The maximum depth of the tree models</param>
        /// <param name="maximumLeafCount">The maximum leaf count of the tree models</param>
        /// <param name="minimumSplitSize">minimum node split size in the trees 1 is default</param>
        /// <param name="minimumInformationGain">The minimum improvement in information gain before a split is made</param>
        /// <param name="subSampleRatio">ratio of observations sampled at each iteration. Default is 1.0. 
        /// If below 1.0 the algorithm changes to stochastic gradient boosting. 
        /// This reduces variance in the ensemble and can help ounter overfitting</param>
        /// <param name="loss">loss function used</param>
        /// <param name="numberOfThreads">Number of threads to use for paralization</param>
        public RegressionGradientBoostLearner(int iterations, double learningRate, int maximumTreeDepth,
            int minimumSplitSize, double minimumInformationGain, double subSampleRatio, IGradientBoostLoss loss, int numberOfThreads)
        {
            if (iterations < 1) { throw new ArgumentException("Iterations must be at least 1"); }
            if (learningRate <= 0.0) { throw new ArgumentException("learning rate must be larger than 0"); }
            if (minimumSplitSize <= 0) { throw new ArgumentException("minimum split size must be larger than 0"); }
            if (maximumTreeDepth < 0) { throw new ArgumentException("maximum tree depth must be larger than 0"); }
            if (minimumInformationGain <= 0) { throw new ArgumentException("minimum information gain must be larger than 0"); }
            if (subSampleRatio <= 0.0 || subSampleRatio > 1.0) { throw new ArgumentException("subSampleRatio must be larger than 0.0 and at max 1.0"); }
            if (loss == null) { throw new ArgumentNullException("loss"); }

            m_iterations = iterations;
            m_learningRate = learningRate;
            m_subSampleRatio = subSampleRatio;
            m_loss = loss;
            m_learner = new GBMDecisionTreeLearner(maximumTreeDepth, minimumSplitSize, minimumInformationGain, m_loss, numberOfThreads);
        }

        /// <summary>
        /// <summary>
        ///  Base regression gradient boost learner. 
        ///  A series of regression trees are fitted stage wise on the residuals of the previous stage
        /// </summary>
        /// <param name="iterations">The number of iterations or stages</param>
        /// <param name="learningRate">How much each iteration should contribute with</param>
        /// <param name="maximumTreeDepth">The maximum depth of the tree models</param>
        /// <param name="maximumLeafCount">The maximum leaf count of the tree models</param>
        /// <param name="minimumSplitSize">minimum node split size in the trees 1 is default</param>
        /// <param name="minimumInformationGain">The minimum improvement in information gain before a split is made</param>
        /// <param name="subSampleRatio">ratio of observations sampled at each iteration. Default is 1.0. 
        /// If below 1.0 the algorithm changes to stochastic gradient boosting. 
        /// This reduces variance in the ensemble and can help ounter overfitting</param>
        public RegressionGradientBoostLearner(int iterations = 100, double learningRate = 0.1, int maximumTreeDepth = 3,
            int minimumSplitSize = 1, double minimumInformationGain = 0.000001, double subSampleRatio = 1.0)
            : this(iterations, learningRate, maximumTreeDepth, minimumSplitSize, minimumInformationGain, 
                subSampleRatio, new GradientBoostSquaredLoss(), Environment.ProcessorCount)
        {
        }

        /// <summary>
        ///  A series of regression trees are fitted stage wise on the residuals of the previous tree
        /// </summary>
        /// <param name="observations"></param>
        /// <param name="targets"></param>
        /// <returns></returns>
        public RegressionGradientBoostModel Learn(F64Matrix observations, double[] targets)
        {
            var allIndices = Enumerable.Range(0, targets.Length).ToArray();
            return Learn(observations, targets, allIndices);
        }

        /// <summary>
        ///  A series of regression trees are fitted stage wise on the residuals of the previous tree
        /// </summary>
        /// <param name="observations"></param>
        /// <param name="targets"></param>
        /// <returns></returns>
        public RegressionGradientBoostModel Learn(F64Matrix observations, double[] targets, int[] indices)
        {
            var rows = observations.GetNumberOfRows();
            var orderedElements = CreateOrderedElements(observations, rows);

            var inSample = targets.Select(t => false).ToArray();
            indices.ForEach(i => inSample[i] = true);
            var workIndices = indices.ToArray();

            var trees = new GBMTree[m_iterations];

            var initialLoss = m_loss.InitialLoss(targets, inSample);
            var predictions = targets.Select(t => initialLoss).ToArray();
            var residuals = new double[targets.Length];

            for (int iteration = 0; iteration < m_iterations; iteration++)
            {
                m_loss.UpdateResiduals(targets, predictions, residuals, inSample);

                var sampleSize = targets.Length;
                if (m_subSampleRatio != 1.0)
                {
                    sampleSize = (int)Math.Round(m_subSampleRatio * workIndices.Length);
                    var currentInSample = Sample(sampleSize, workIndices, targets.Length);
                    
                    trees[iteration] = m_learner.Learn(observations, targets, residuals,
                        predictions, orderedElements, currentInSample);

                }
                else
                {
                    trees[iteration] = m_learner.Learn(observations, targets, residuals,
                        predictions, orderedElements, inSample);
                }

                var predict = trees[iteration].Predict(observations);
                for (int i = 0; i < predict.Length; i++)
                {
                    predictions[i] += m_learningRate * predict[i];
                }
            }

            return new RegressionGradientBoostModel(trees, m_learningRate, initialLoss, observations.GetNumberOfColumns());
        }

        /// <summary>
        /// Learns a RegressionGradientBoostModel with early stopping.
        /// The parameter earlyStoppingRounds controls how often the validation error is measured.
        /// If the validation error has increased, the learning is stopped and the model with the best number of iterations (trees) is returned.
        /// The number of iterations used is equal to the number of trees in the resulting model.
        /// The method used for early stopping is based on the article:
        /// http://page.mi.fu-berlin.de/prechelt/Biblio/stop_tricks1997.pdf
        /// </summary>
        /// <param name="trainingObservations"></param>
        /// <param name="trainingTargets"></param>
        /// <param name="validationObservations"></param>
        /// <param name="validationTargets"></param>
        /// <param name="metric">The metric to use for early stopping</param>
        /// <param name="earlyStoppingRounds">The number of rounds the error is allowed not to decline before early stopping. 
        /// The resulting model and iteration will be the last with the error declining</param>
        /// <returns>RegressionGradientBoostModel with early stopping. The number of iterations will equal the number of trees in the model</returns>
        public RegressionGradientBoostModel LearnWithEarlyStopping(F64Matrix trainingObservations, double[] trainingTargets,
            F64Matrix validationObservations, double[] validationTargets,
            IMetric<double, double> metric, int earlyStoppingRounds)
        {
            var rows = trainingObservations.GetNumberOfRows();
            var orderedElements = CreateOrderedElements(trainingObservations, rows);

            var inSample = trainingTargets.Select(t => false).ToArray();
            var indices = Enumerable.Range(0, trainingTargets.Length).ToArray();
            indices.ForEach(i => inSample[i] = true);
            var workIndices = indices.ToArray();

            var trees = new GBMTree[m_iterations];

            var initialLoss = m_loss.InitialLoss(trainingTargets, inSample);
            var predictions = trainingTargets.Select(t => initialLoss).ToArray();
            var residuals = new double[trainingTargets.Length];

            var bestIterationCount = 0;
            var currentBedstError = double.MaxValue;

            for (int iteration = 0; iteration < m_iterations; iteration++)
            {
                m_loss.UpdateResiduals(trainingTargets, predictions, residuals, inSample);

                var sampleSize = trainingTargets.Length;
                if (m_subSampleRatio != 1.0)
                {
                    sampleSize = (int)Math.Round(m_subSampleRatio * workIndices.Length);
                    var currentInSample = Sample(sampleSize, workIndices, trainingTargets.Length);

                    trees[iteration] = m_learner.Learn(trainingObservations, trainingTargets, residuals,
                        predictions, orderedElements, currentInSample);

                }
                else
                {
                    trees[iteration] = m_learner.Learn(trainingObservations, trainingTargets, residuals,
                        predictions, orderedElements, inSample);
                }

                var predict = trees[iteration].Predict(trainingObservations);
                for (int i = 0; i < predict.Length; i++)
                {
                    predictions[i] += m_learningRate * predict[i];
                }

                // When using early stopping, Check that the validation error is not increasing between earlyStoppingRounds
                // If the validation error has increased, stop the learning and return the model with the best number of iterations (trees).
                if (iteration % earlyStoppingRounds == 0)
                {
                    var model = new RegressionGradientBoostModel(trees.Take(iteration).ToArray(), m_learningRate, initialLoss, trainingObservations.GetNumberOfColumns());
                    var validPredictions = model.Predict(validationObservations);
                    var error = metric.Error(validationTargets, validPredictions);

                    if (currentBedstError > error)
                    {
                        currentBedstError = error;
                        bestIterationCount = iteration;
                    }
                    else
                    {
                        break;
                    }
                }
            }

            return new RegressionGradientBoostModel(trees.Take(bestIterationCount).ToArray(),
                m_learningRate, initialLoss, trainingObservations.GetNumberOfColumns());
        }

        /// <summary>
        /// Private explicit interface implementation for indexed learning.
        /// </summary>
        /// <param name="observations"></param>
        /// <param name="targets"></param>
        /// <param name="indices"></param>
        /// <returns></returns>
        IPredictor<double> IIndexedLearner<double>.Learn(F64Matrix observations, double[] targets, int[] indices)
        {
            return Learn(observations, targets, indices);
        }

        /// <summary>
        /// Private explicit interface implementation for indexed learning.
        /// </summary>
        /// <param name="observations"></param>
        /// <param name="targets"></param>
        /// <returns></returns>
        IPredictor<double> ILearner<double>.Learn(F64Matrix observations, double[] targets)
        {
            return Learn(observations, targets);
        }


        /// <summary>
        /// Creates a matrix of ordered indices. Each row is ordered after the corresponding feature column.
        /// </summary>
        /// <param name="observations"></param>
        /// <param name="rows"></param>
        /// <returns></returns>
        int[][] CreateOrderedElements(F64Matrix observations, int rows)
        {
            var orderedElements = new int[observations.GetNumberOfColumns()][];

            for (int i = 0; i < observations.GetNumberOfColumns(); i++)
            {
                var feature = observations.GetColumn(i);
                var indices = Enumerable.Range(0, rows).ToArray();
                feature.SortWith(indices);
                orderedElements[i] = indices;
            }
            return orderedElements;
        }

        /// <summary>
        /// Creates a bool array with the selected samples (true)
        /// </summary>
        /// <param name="sampleSize"></param>
        /// <param name="indices"></param>
        /// <returns></returns>
        bool[] Sample(int sampleSize, int[] indices, int allObservationCount)
        {
            var inSample = new bool[allObservationCount];
            indices.Shuffle(m_random);

            for (int i = 0; i < sampleSize; i++)
            {
                inSample[indices[i]] = true;
            }

            return inSample;
        }
    }
}
