﻿using Grand.Business.Core.Interfaces.Checkout.Orders;
using Grand.Business.Core.Interfaces.Common.Addresses;
using Grand.Business.Core.Interfaces.Common.Directory;
using Grand.Business.Core.Interfaces.Common.Localization;
using Grand.Business.Core.Queries.Checkout.Orders;
using Grand.Domain.Common;
using Grand.Domain.Orders;
using Grand.Infrastructure;
using Grand.Web.Commands.Models.Orders;
using Grand.Web.Common.Filters;
using Grand.Web.Extensions;
using Grand.Web.Features.Models.Common;
using Grand.Web.Features.Models.Orders;
using Grand.Web.Models.Common;
using Grand.Web.Models.Orders;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Grand.Web.Controllers
{
    [DenySystemAccount]
    public class MerchandiseReturnController : BasePublicController
    {
        #region Fields

        private readonly IMerchandiseReturnService _merchandiseReturnService;
        private readonly IOrderService _orderService;
        private readonly IWorkContext _workContext;
        private readonly IGroupService _groupService;
        private readonly ITranslationService _translationService;
        private readonly IMediator _mediator;
        private readonly AddressSettings _addressSettings;
        private readonly OrderSettings _orderSettings;
        #endregion

        #region Constructors

        public MerchandiseReturnController(
            IMerchandiseReturnService merchandiseReturnService,
            IOrderService orderService,
            IWorkContext workContext,
            IGroupService groupService,
            ITranslationService translationService,
            IMediator mediator,
            AddressSettings addressSettings,
            OrderSettings orderSettings)
        {
            _merchandiseReturnService = merchandiseReturnService;
            _orderService = orderService;
            _workContext = workContext;
            _groupService = groupService;
            _translationService = translationService;
            _mediator = mediator;
            _addressSettings = addressSettings;
            _orderSettings = orderSettings;
        }

        #endregion

        #region Utilities

        private async Task PrepareModelAddress(AddressModel addressModel, Address address)
        {
            var countryService = HttpContext.RequestServices.GetRequiredService<ICountryService>();
            var countries = await countryService.GetAllCountries(_workContext.WorkingLanguage.Id, _workContext.CurrentStore.Id);
            addressModel = await _mediator.Send(new GetAddressModel {
                Language = _workContext.WorkingLanguage,
                Store = _workContext.CurrentStore,
                Customer = _workContext.CurrentCustomer,
                Model = addressModel,
                Address = address,
                ExcludeProperties = true,
                PrePopulateWithCustomerFields = true,
                LoadCountries = () => countries
            });
        }

        protected async Task<Address> PrepareAddress(MerchandiseReturnModel model)
        {
            var address = new Address();
            if (!_orderSettings.MerchandiseReturns_AllowToSpecifyPickupAddress) return address;
            if (!string.IsNullOrEmpty(model.PickupAddressId))
            {
                address = _workContext.CurrentCustomer.Addresses.FirstOrDefault(a => a.Id == model.PickupAddressId);
            }
            else
            {
                var customAttributes = await _mediator.Send(new GetParseCustomAddressAttributes { SelectedAttributes = model.MerchandiseReturnNewAddress.SelectedAttributes });
                var addressAttributeParser = HttpContext.RequestServices.GetRequiredService<IAddressAttributeParser>();
                var customAttributeWarnings = await addressAttributeParser.GetAttributeWarnings(customAttributes);
                foreach (var error in customAttributeWarnings)
                {
                    ModelState.AddModelError("", error);
                }
                address = model.MerchandiseReturnNewAddress.ToEntity(_workContext.CurrentCustomer, _addressSettings);
                model.NewAddressPreselected = true;
                address.Attributes = customAttributes;
                address.CreatedOnUtc = DateTime.UtcNow;
            }
            return address;
        }


        #endregion

        #region Methods

        public virtual async Task<IActionResult> CustomerMerchandiseReturns()
        {
            if (!await _groupService.IsRegistered(_workContext.CurrentCustomer))
                return Challenge();

            var model = await _mediator.Send(new GetMerchandiseReturns {
                Customer = _workContext.CurrentCustomer,
                Store = _workContext.CurrentStore,
                Language = _workContext.WorkingLanguage
            });

            return View(model);
        }

        public virtual async Task<IActionResult> MerchandiseReturn(string orderId, string errors = "")
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            if (!await _mediator.Send(new IsMerchandiseReturnAllowedQuery { Order = order }))
                return RedirectToRoute("HomePage");

            var model = await _mediator.Send(new GetMerchandiseReturn {
                Order = order,
                Language = _workContext.WorkingLanguage,
                Store = _workContext.CurrentStore
            });
            model.Error = errors;
            return View(model);
        }

        [HttpPost, ActionName("MerchandiseReturn")]
        [AutoValidateAntiforgeryToken]
        public virtual async Task<IActionResult> MerchandiseReturnSubmit(string orderId, MerchandiseReturnModel model)
        {
            var order = await _orderService.GetOrderById(orderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            if (!await _mediator.Send(new IsMerchandiseReturnAllowedQuery { Order = order }))
                return RedirectToRoute("HomePage");

            if (_orderSettings.MerchandiseReturns_AllowToSpecifyPickupDate && _orderSettings.MerchandiseReturns_PickupDateRequired && model.PickupDate == null)
            {
                ModelState.AddModelError("", _translationService.GetResource("MerchandiseReturns.PickupDateRequired"));
            }

            var address = await PrepareAddress(model);

            if (ModelState is { IsValid: false })
            {
                var returnModel = await _mediator.Send(new GetMerchandiseReturn {
                    Order = order,
                    Language = _workContext.WorkingLanguage,
                    Store = _workContext.CurrentStore
                });
                returnModel.Error = string.Join(", ", ModelState.Keys.SelectMany(k => ModelState[k]!.Errors).Select(m => m.ErrorMessage).ToArray());
                returnModel.Comments = model.Comments;
                returnModel.PickupDate = model.PickupDate;
                returnModel.NewAddressPreselected = model.NewAddressPreselected;
                returnModel.MerchandiseReturnNewAddress = model.MerchandiseReturnNewAddress;
                if (returnModel.NewAddressPreselected || _orderSettings.MerchandiseReturns_AllowToSpecifyPickupAddress)
                {
                    await PrepareModelAddress(model.MerchandiseReturnNewAddress, address);
                }
                return View(returnModel);
            }
            else
            {
                var result = await _mediator.Send(new MerchandiseReturnSubmitCommand { Address = address, Model = model, Order = order });
                if (result.rr.ReturnNumber > 0)
                {
                    model.Result = string.Format(_translationService.GetResource("MerchandiseReturns.Submitted"), result.rr.ReturnNumber, Url.Link("MerchandiseReturnDetails", new { merchandiseReturnId = result.rr.Id }));
                    model.OrderNumber = order.OrderNumber;
                    model.OrderCode = order.Code;
                    return View(result.model);
                }

                var returnModel = await _mediator.Send(new GetMerchandiseReturn {
                    Order = order,
                    Language = _workContext.WorkingLanguage,
                    Store = _workContext.CurrentStore
                });
                returnModel.Error = result.model.Error;
                returnModel.Comments = model.Comments;
                returnModel.PickupDate = model.PickupDate;
                returnModel.NewAddressPreselected = model.NewAddressPreselected;
                returnModel.MerchandiseReturnNewAddress = model.MerchandiseReturnNewAddress;
                if (returnModel.NewAddressPreselected || _orderSettings.MerchandiseReturns_AllowToSpecifyPickupAddress)
                {
                    await PrepareModelAddress(model.MerchandiseReturnNewAddress, address);
                }
                return View(returnModel);
            }
        }

        public virtual async Task<IActionResult> MerchandiseReturnDetails(string merchandiseReturnId)
        {
            var rr = await _merchandiseReturnService.GetMerchandiseReturnById(merchandiseReturnId);
            if (!await rr.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var order = await _orderService.GetOrderById(rr.OrderId);
            if (!await order.Access(_workContext.CurrentCustomer, _groupService))
                return Challenge();

            var model = await _mediator.Send(new GetMerchandiseReturnDetails {
                Order = order,
                Language = _workContext.WorkingLanguage,
                MerchandiseReturn = rr
            });

            return View(model);
        }

        #endregion
    }
}
