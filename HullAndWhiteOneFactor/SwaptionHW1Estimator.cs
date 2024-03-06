/* Copyright (C) 2009-2014 Fairmat SRL (info@fairmat.com, http://www.fairmat.com/)
 * Author(s): Enrico Degiuli (enrico.degiuli@fairmat.com)
 *            Matteo Tesser (matteo.tesser@fairmat.com)
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using DVPLDOM;
using DVPLI;
using Fairmat.Finance;
using Fairmat.Optimization;
using Fairmat.Calibration;
using Fairmat.MarketData;
using Mono.Addins;

namespace HullAndWhiteOneFactor
{
    /// <summary>
    /// Implementation of HW1 Calibration (swaption matrix based).
    /// </summary>
    [Extension("/Fairmat/Estimator")]
    public class SwaptionHWEstimator : IEstimatorEx, IMenuItemDescription
    {
        /// <summary>
        /// Gets the tooltip for the implemented calibration function.
        /// </summary>
        public string ToolTipText
        {
            get
            {
                return "Calibrate Using Swaption";
            }
        }

        /// <summary>
        /// Gets the description of the implemented calibration function.
        /// </summary>
        public virtual string Description
        {
            get
            {
                return "Calibrate HW1 using Swaption";
            }
        }

        #region IEstimator Members

        /// <summary>
        /// Gets the value requested by the interface ProvidesTo,
        /// returning HW1 as the type.
        /// </summary>
        public virtual Type ProvidesTo
        {
            get
            {
                return typeof(HW1);
            }
        }

        /// <summary>
        /// Gets the types required by the estimator in order to work:
        /// InterestRateMarketData is the only required type for this estimator.
        /// </summary>
        /// <param name="settings">The parameter is not used.</param>
        /// <param name="multivariateRequest">The parameter is not used.</param>
        /// <returns>An array containing the type InterestRateMarketData.</returns>
        virtual public EstimateRequirement[] GetRequirements(IEstimationSettings settings, EstimateQuery query)
        {
            return new EstimateRequirement[] { new EstimateRequirement(typeof(InterestRateMarketData)) };
        }

        protected virtual BlackModel BlackModelFactory(Function zr)
        {
            return new BlackModel(zr);
        }


        /// <summary>
        /// Attempts a calibration through <see cref="SwaptionHW1OptimizationProblem"/>
        /// using swaption matrices.
        /// </summary>
        /// <param name="data">The data to be used in order to perform the calibration.</param>
        /// <param name="settings">The parameter is not used.</param>
        /// <param name="controller">The controller which may be used to cancel the process.</param>
        /// <returns>The results of the calibration.</returns>
        public EstimationResult Estimate(List<object> data, IEstimationSettings settings = null, IController controller = null, Dictionary<string, object> properties = null)
        {
            InterestRateMarketData dataset = data[0] as InterestRateMarketData;
            MatrixMarketData normalVol = null;
            if (data.Count > 1)
                normalVol = (MatrixMarketData)data[1];

            PFunction zr = new PFunction(null);
            // Loads the zero rate.
            double[,] zrvalue = (double[,])ArrayHelper.Concat(dataset.ZRMarketDates.ToArray(), dataset.ZRMarket.ToArray());
            zr.Expr = zrvalue;

            //todo: move to common data quality code
            if (dataset.SwaptionTenor == 0)
            {
                Console.WriteLine("Warning SwaptionTenor not set, using default (1)");
                dataset.SwaptionTenor = 1;
            }


            double deltak = dataset.SwaptionTenor;
            Console.WriteLine("Swaption Tenor\t" + dataset.SwaptionTenor);
           
            var swaptionsFiltering = settings as SwaptionsFiltering;

            if (swaptionsFiltering == null)
                swaptionsFiltering = new SwaptionsFiltering();//creates a default

            //F stands for Full matrix
            var optionMaturityF = normalVol!=null ? normalVol.RowValues: dataset.OptionMaturity;
            var swapDurationF = normalVol != null ? normalVol.ColumnValues: dataset.SwapDuration;
            var swaptionsVolatilityF = normalVol != null ? normalVol.Values: dataset.SwaptionsVolatility;

            int maturitiesCount = optionMaturityF.Count(x => x >= swaptionsFiltering.MinSwaptionMaturity && x <= swaptionsFiltering.MaxSwaptionMaturity);
            int durationsCount = swapDurationF.Count(x => x >= swaptionsFiltering.MinSwapDuration && x <= swaptionsFiltering.MaxSwapDuration);

            
            Console.WriteLine(string.Format("Calibrating on {0} swaptions prices [#maturiries x #durations]=[{1} x {2}]", maturitiesCount * durationsCount, maturitiesCount,durationsCount));

            if (maturitiesCount * durationsCount == 0)
                return new EstimationResult("No swaptions satisfying criteria found, please relax filters");

            //reduced version
            var swaptionsVolatility = new Matrix(maturitiesCount, durationsCount);// dataset.SwaptionsVolatility;
            var optionMaturity = new Vector(maturitiesCount);// dataset.OptionMaturity;
            var swapDuration = new Vector(durationsCount);// dataset.SwapDuration;
            

            //Build filtered matrix and vectors
            int fm=0;
            for (int m = 0; m < optionMaturityF.Length; m++)
            {
                int fd=0;
                if (optionMaturityF[m] >= swaptionsFiltering.MinSwaptionMaturity && optionMaturityF[m] <= swaptionsFiltering.MaxSwaptionMaturity)
                {
                    for (int d = 0; d < swapDurationF.Length; d++)
                    {
                        if (swapDurationF[d] >= swaptionsFiltering.MinSwapDuration && swapDurationF[d] <= swaptionsFiltering.MaxSwapDuration)
                        {   
                            swaptionsVolatility[fm, fd] = swaptionsVolatilityF[m, d];
                            swapDuration[fd] = swapDurationF[d];
                            fd++; }
                    }

                    optionMaturity[fm] = optionMaturityF[m];
                    fm++;
                }

            }

            var swbm = new SwaptionsBlackModel(zr,BlackModelFactory(zr));

            Matrix fsr;
            var blackSwaptionPrice = 1000.0 * swbm.SwaptionsSurfBM(optionMaturity, swapDuration, swaptionsVolatility, deltak, out fsr);


            Console.WriteLine("Maturities\t"+optionMaturity);
            Console.WriteLine("swapDuration\t"+swapDuration);
            Console.WriteLine("SwaptionHWEstimator: Black model prices");
            Console.WriteLine(blackSwaptionPrice);

            SwaptionHW1 swhw1 = new SwaptionHW1(zr);
            SwaptionHW1OptimizationProblem problem = new SwaptionHW1OptimizationProblem(swhw1, blackSwaptionPrice, optionMaturity, swapDuration, deltak);

            IOptimizationAlgorithm solver = new QADE();
            IOptimizationAlgorithm solver2 = new SteepestDescent();

            DESettings o = new DESettings();
            o.NP = 20;
            o.MaxIter = 5;
            o.Verbosity = 1;
            o.controller = controller;
            SolutionInfo solution = null;

            Vector x0 = new Vector(new double[] { 0.1, 0.1 });
            solution = solver.Minimize(problem, o, x0);
            if (solution.errors)
                return new EstimationResult(solution.message);

            o.epsilon = 10e-8;
            o.h = 10e-8;
            o.MaxIter = 1000;

            // We can permit this, given it is fast.
            o.accourate_numerical_derivatives = true;

            if (solution != null)
                solution = solver2.Minimize(problem, o, solution.x);
            else
                solution = solver2.Minimize(problem, o, x0);
            if (solution.errors)
                return new EstimationResult(solution.message);
            Console.WriteLine("Solution:");
            Console.WriteLine(solution);
            string[] names = new string[] { "Alpha", "Sigma" };

            Console.WriteLine("SwaptionHWEstimator: hw model prices and error");
            problem.Obj(solution.x,true);

            EstimationResult result = new EstimationResult(names, solution.x);

            result.ZRX = (double[])dataset.ZRMarketDates.ToArray();
            result.ZRY = (double[])dataset.ZRMarket.ToArray();

            double obj = problem.Obj(solution.x);

            return result;
        }
        #endregion

        public IEstimationSettings DefaultSettings
        {
            get { return UserSettings.GetSettings(typeof(SwaptionsFiltering)) as SwaptionsFiltering; }
        }
    }
}
