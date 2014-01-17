﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GeoAPI.Geometries;
using SharpMap.Data;
using SharpMap.Layers;

namespace SharpMap.Web.Wms.Server.Handlers
{
    public abstract class AbstractHandler : IHandler
    {
        protected const StringComparison Case = StringComparison.InvariantCultureIgnoreCase;

        private readonly Capabilities.WmsServiceDescription _description;

        protected AbstractHandler(Capabilities.WmsServiceDescription description)
        {
            _description = description;
        }        

        protected Capabilities.WmsServiceDescription Description
        {
            get { return _description; }
        }

        internal static IHandler For(string request, Capabilities.WmsServiceDescription description)
        {
            const StringComparison comparison = StringComparison.InvariantCultureIgnoreCase;
            if (String.Equals(request, "GetCapabilities", comparison))
                return new GetCapabilities(description);
            if (String.Equals(request, "GetFeatureInfo", comparison) )
                return new GetFeatureInfo(description);
            if (String.Equals(request, "GetMap", comparison))
                return new GetMap(description);
            return null;
        }

        /// <summary>
        /// Parses a boundingbox string to a boundingbox geometry from the format minx,miny,maxx,maxy. Returns null if the format is invalid
        /// </summary>
        /// <param name="boundingBox">string representation of a boundingbox</param>
        /// <param name="flipXY">Value indicating that x- and y-ordinates should be changed.</param>
        /// <returns>Boundingbox or null if invalid parameter</returns>
        public static Envelope ParseBBOX(string boundingBox, bool flipXY)            
        {
            const NumberStyles ns = NumberStyles.Float;
            NumberFormatInfo nf = Map.NumberFormatEnUs;

            string[] strVals = boundingBox.Split(new[] { ',' });
            if (strVals.Length != 4)
                return null;

            double minx, miny, maxx, maxy;            
            if (!Double.TryParse(strVals[0], ns, nf, out minx))
                return null;
            if (!Double.TryParse(strVals[2], ns, nf, out maxx))
                return null;
            if (maxx < minx)
                return null;
            if (!Double.TryParse(strVals[1], ns, nf, out miny))
                return null;
            if (!Double.TryParse(strVals[3], ns, nf, out maxy))
                return null;
            if (maxy < miny)
                return null;

            if (flipXY) 
                return new Envelope(miny, maxy, minx, maxx);
            return new Envelope(minx, maxx, miny, maxy);
        }

        public abstract void Handle(Map map, IContext context);

        protected abstract WmsParams ValidateParams(IContext context, int targetSrid);

        /// <summary>
        /// Validate common arguments for GetFeatureInfo and GetMap requests
        /// </summary>
        protected WmsParams ValidateCommons(IContext context, int targetSrid)
        {
            string version = context.Params["VERSION"];
            if (version == null)
                return WmsParams.Failure("VERSION parameter not supplied");
            if (!String.Equals(version, "1.3.0", Case))
                return WmsParams.Failure("Only version 1.3.0 supported");
            string layers = context.Params["LAYERS"];
            if (layers == null)
                return WmsParams.Failure("Required parameter LAYERS not specified");
            string styles = context.Params["STYLES"];
            if (styles == null)
                return WmsParams.Failure("Required parameter STYLES not specified");
            string crs = context.Params["CRS"];
            if (crs == null)
                return WmsParams.Failure("Required parameter CRS not specified");
            if (crs != "EPSG:" + targetSrid)
                return WmsParams.Failure(WmsException.WmsExceptionCode.InvalidCRS, "CRS not supported");
            string bbox = context.Params["BBOX"];
            if (bbox == null)
                return WmsParams.Failure(WmsException.WmsExceptionCode.InvalidDimensionValue,
                    "Required parameter BBOX not (");
            string width = context.Params["WIDTH"];
            if (width == null)
                return WmsParams.Failure(WmsException.WmsExceptionCode.InvalidDimensionValue,
                    "Required parameter WIDTH not specified");
            string height = context.Params["HEIGHT"];
            if (height == null)
                return WmsParams.Failure(WmsException.WmsExceptionCode.InvalidDimensionValue,
                    "Required parameter HEIGHT not specified");
            string format = context.Params["FORMAT"];
            if (format == null)
                return WmsParams.Failure("Required parameter FORMAT not specified");
            string cqlFilter = context.Params["CQL_FILTER"];
            short w, h;
            if (!Int16.TryParse(width, out w) || !Int16.TryParse(height, out h))
                return WmsParams.Failure("Invalid parameters for HEIGHT or WITDH");
            Envelope env = ParseBBOX(bbox, targetSrid == 4326);
            if (env == null)
                return WmsParams.Failure("Invalid parameter BBOX");

            return new WmsParams
            {
                Layers = layers,
                Styles = styles,
                CRS = crs,
                BBOX = env,
                Width = w,
                Height = h,
                Format = format,
                CqlFilter = cqlFilter
            };
        }

        /// <summary>
        /// Filters the features to be processed by a CQL filter
        /// </summary>
        /// <param name="row">A <see cref="T:SharpMap.Data.FeatureDataRow"/> to test.</param>
        /// <param name="cqlString">A CQL string defining the filter </param>
        /// <returns>GeoJSON string with featureinfo results</returns>
        public bool CqlFilter(FeatureDataRow row, string cqlString)
        {
            bool toreturn = true;
            //check on filter type (AND, OR, NOT)
            string[] splitstring = { " " };
            string[] cqlStringItems = cqlString.Split(splitstring, StringSplitOptions.RemoveEmptyEntries);
            string[] comparers = { "==", "!=", "<", ">", "<=", ">=", "BETWEEN", "LIKE", "IN" };
            for (int i = 0; i < cqlStringItems.Length; i++)
            {
                bool tmpResult = true;
                //check first on AND OR NOT, only the case if multiple checks have to be done
                // ReSharper disable InconsistentNaming
                bool AND = true;
                bool OR = false;
                bool NOT = false;
                // ReSharper restore InconsistentNaming
                if (cqlStringItems[i] == "AND") { i++; }
                if (cqlStringItems[i] == "OR") { AND = false; OR = true; i++; }
                if (cqlStringItems[i] == "NOT") { AND = false; NOT = true; i++; }
                if ((NOT && !toreturn) || (AND && !toreturn))
                    break;
                //valid cql starts always with the column name
                string column = cqlStringItems[i];
                int columnIndex = row.Table.Columns.IndexOf(column);
                Type t = row.Table.Columns[columnIndex].DataType;
                if (columnIndex < 0)
                    break;
                i++;
                string comparer = cqlStringItems[i];
                i++;
                //if the comparer isn't in the comparerslist stop
                if (!comparers.Contains(comparer))
                    break;

                if (comparer == comparers[8])//IN 
                {
                    //read all the items until the list is closed by ')' and merge them
                    //all items are assumed to be separated by space merge them first
                    //items are merged because a item might contain a space itself, and in this case it's splitted at the wrong place
                    string IN = "";
                    while (!cqlStringItems[i].Contains(")"))
                    {
                        IN = IN + " " + cqlStringItems[i];
                        i++;
                    }
                    IN = IN + " " + cqlStringItems[i];
                    string[] splitters = { "('", "', '", "','", "')" };
                    List<string> items = IN.Split(splitters, StringSplitOptions.RemoveEmptyEntries).ToList();

                    tmpResult = items.Contains(Convert.ToString(row[columnIndex]));
                }
                else if (comparer == comparers[7])//LIKE
                {
                    //to implement
                    //tmpResult = true;
                }
                else if (comparer == comparers[6])//BETWEEN
                {
                    //get type number of string
                    if (t == typeof(string))
                    {
                        string string1 = cqlStringItems[i];
                        i += 2; //skip the AND in BETWEEN
                        string string2 = cqlStringItems[i];
                        tmpResult = 0 < String.Compare(Convert.ToString(row[columnIndex], NumberFormatInfo.InvariantInfo), string1, StringComparison.Ordinal) &&
                                    0 > String.Compare(Convert.ToString(row[columnIndex], NumberFormatInfo.InvariantInfo), string2, StringComparison.Ordinal);

                    }
                    else if (t == typeof(double))
                    {
                        double value1 = Convert.ToDouble(cqlStringItems[i]);
                        i += 2; //skip the AND in BETWEEN
                        double value2 = Convert.ToDouble(cqlStringItems[i]);
                        tmpResult = value1 < Convert.ToDouble(row[columnIndex]) && value2 > Convert.ToDouble(row[columnIndex]);
                    }
                    else if (t == typeof(int))
                    {
                        int value1 = Convert.ToInt32(cqlStringItems[i]);
                        i += 2;
                        int value2 = Convert.ToInt32(cqlStringItems[i]);
                        tmpResult = value1 < Convert.ToInt32(row[columnIndex]) && value2 > Convert.ToInt32(row[columnIndex]);
                    }
                }
                else
                {
                    if (t == typeof(string))
                    {
                        string cqlValue = Convert.ToString(cqlStringItems[i], NumberFormatInfo.InvariantInfo);
                        string rowValue = Convert.ToString(row[columnIndex], NumberFormatInfo.InvariantInfo);
                        if (comparer == comparers[5])//>=
                        {
                            tmpResult = 0 <= String.Compare(rowValue, cqlValue, StringComparison.Ordinal);
                        }
                        else if (comparer == comparers[4])//<=
                        {
                            tmpResult = 0 >= String.Compare(rowValue, cqlValue, StringComparison.Ordinal);
                        }
                        else if (comparer == comparers[3])//>
                        {
                            tmpResult = 0 < String.Compare(rowValue, cqlValue, StringComparison.Ordinal);
                        }
                        else if (comparer == comparers[2])//<
                        {
                            tmpResult = 0 > String.Compare(rowValue, cqlValue, StringComparison.Ordinal);
                        }
                        else if (comparer == comparers[1])//!=
                        {
                            tmpResult = rowValue != cqlValue;
                        }
                        else if (comparer == comparers[0])//==
                        {
                            tmpResult = rowValue == cqlValue;
                        }
                    }
                    else
                    {
                        double value = Convert.ToDouble(cqlStringItems[i]);
                        if (comparer == comparers[5])//>=
                        {
                            tmpResult = Convert.ToDouble(row[columnIndex]) >= value;
                        }
                        else if (comparer == comparers[4])//<=
                        {
                            tmpResult = Convert.ToDouble(row[columnIndex]) <= value;
                        }
                        else if (comparer == comparers[3])//>
                        {
                            tmpResult = Convert.ToDouble(row[columnIndex]) > value;
                        }
                        else if (comparer == comparers[2])//<
                        {
                            tmpResult = Convert.ToDouble(row[columnIndex]) < value;
                        }
                        else if (comparer == comparers[1])//!=
                        {
                            tmpResult = Convert.ToDouble(row[columnIndex]) != value;
                        }
                        else if (comparer == comparers[0])//==
                        {
                            tmpResult = Convert.ToDouble(row[columnIndex]) == value;
                        }
                    }
                }
                if (AND)
                    toreturn = tmpResult;
                if (OR && tmpResult)
                    toreturn = true;
                if (toreturn && NOT && tmpResult)
                    toreturn = false;

            }
            //OpenLayers.Filter.Comparison.EQUAL_TO = “==”;
            //OpenLayers.Filter.Comparison.NOT_EQUAL_TO = “!=”;
            //OpenLayers.Filter.Comparison.LESS_THAN = “<”;
            //OpenLayers.Filter.Comparison.GREATER_THAN = “>”;
            //OpenLayers.Filter.Comparison.LESS_THAN_OR_EQUAL_TO = “<=”;
            //OpenLayers.Filter.Comparison.GREATER_THAN_OR_EQUAL_TO = “>=”;
            //OpenLayers.Filter.Comparison.BETWEEN = “..”;
            //OpenLayers.Filter.Comparison.LIKE = “~”;
            //IN (,,)

            return toreturn;
        }

        protected int TargetSrid(Map map)
        {
            if (map == null) 
                throw new ArgumentNullException("map");
            ILayer layer = map.Layers.FirstOrDefault();
            if (layer == null)
                throw new ArgumentException("no layers defined");
            return layer.TargetSRID;
        }
    }
}