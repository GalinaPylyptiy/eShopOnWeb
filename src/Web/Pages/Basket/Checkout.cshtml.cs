using System.Text;
using System.Text.Json;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.CodeAnalysis.Scripting.Hosting;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.Infrastructure.Identity;
using Microsoft.eShopWeb.Web.Interfaces;

namespace Microsoft.eShopWeb.Web.Pages.Basket;

[Authorize]
public class CheckoutModel : PageModel
{
    private readonly IBasketService _basketService;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IOrderService _orderService;
    private string? _username = null;
    private readonly IBasketViewModelService _basketViewModelService;
    private readonly IAppLogger<CheckoutModel> _logger;
    private readonly IConfiguration _configuration;

    public CheckoutModel(IBasketService basketService,
        IBasketViewModelService basketViewModelService,
        SignInManager<ApplicationUser> signInManager,
        IOrderService orderService,
        IAppLogger<CheckoutModel> logger,
        IConfiguration configuration)

    {
        _basketService = basketService;
        _signInManager = signInManager;
        _orderService = orderService;
        _basketViewModelService = basketViewModelService;
        _logger = logger;
        _configuration = configuration;
    }

    public BasketViewModel BasketModel { get; set; } = new BasketViewModel();

    public async Task OnGet()
    {
        await SetBasketModelAsync();
    }

    public async Task<IActionResult> OnPost(IEnumerable<BasketItemViewModel> items)
    {
        try
        {
            await SetBasketModelAsync();

            if (!ModelState.IsValid)
            {
                return BadRequest();
            }

            var updateModel = items.ToDictionary(b => b.Id.ToString(), b => b.Quantity);
            await _basketService.SetQuantities(BasketModel.Id, updateModel);
            var order = await _orderService.CreateOrderAsync(BasketModel.Id, new Address("123 Main St.", "Kent", "OH", "United States", "44240"));
            //  Send order details to Azure Function
            await SendOrderDetailsToServiceBus(order, items);
            await ProcessDeliveryOrder(order, items);
            await _basketService.DeleteBasketAsync(BasketModel.Id);
        }
        catch (EmptyBasketOnCheckoutException emptyBasketOnCheckoutException)
        {
            //Redirect to Empty Basket page
            _logger.LogWarning(emptyBasketOnCheckoutException.Message);
            return RedirectToPage("/Basket/Index");
        }

        return RedirectToPage("Success");
    }

    private async Task SetBasketModelAsync()
    {
        Guard.Against.Null(User?.Identity?.Name, nameof(User.Identity.Name));
        if (_signInManager.IsSignedIn(HttpContext.User))
        {
            BasketModel = await _basketViewModelService.GetOrCreateBasketForUser(User.Identity.Name);
        }
        else
        {
            GetOrSetBasketCookieAndUserName();
            BasketModel = await _basketViewModelService.GetOrCreateBasketForUser(_username!);
        }
    }

    private void GetOrSetBasketCookieAndUserName()
    {
        if (Request.Cookies.ContainsKey(Constants.BASKET_COOKIENAME))
        {
            _username = Request.Cookies[Constants.BASKET_COOKIENAME];
        }
        if (_username != null) return;

        _username = Guid.NewGuid().ToString();
        var cookieOptions = new CookieOptions();
        cookieOptions.Expires = DateTime.Today.AddYears(10);
        Response.Cookies.Append(Constants.BASKET_COOKIENAME, _username, cookieOptions);
    }


    private async Task ProcessDeliveryOrder(ApplicationCore.Entities.OrderAggregate.Order order, IEnumerable<BasketItemViewModel> items)
    {
        Console.WriteLine("Process Delivery Order");
        var orderRequest = new
        {
            id = Guid.NewGuid().ToString(), 
            OrderId = order.Id,
            shippingAddress = new
            {
                Street = order.ShipToAddress.Street,
                City = order.ShipToAddress.City,
                State = order.ShipToAddress.State,
                Country = order.ShipToAddress.Country,
                Zipcode = order.ShipToAddress.ZipCode
            },
            items = order.OrderItems.Select(oi => new { ItemId = oi.Id, Quantity = oi.Units }).ToList(),
            totalPrice = order.OrderItems.Sum(oi => oi.UnitPrice * oi.Units),
            createdAt = DateTime.UtcNow
        };
       
        try
        {
            using var httpClient = new HttpClient();
            var json = JsonSerializer.Serialize(orderRequest, new JsonSerializerOptions
            {
                WriteIndented = true  
            });
            var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
            Console.WriteLine("Order created: " + json);
            var functionUrl = _configuration["DeliveryOrderFunctionUrl"];
            functionUrl = functionUrl + "/api/DeliveryOrderProcessor"; // inject IConfiguration in constructor
            Console.WriteLine("URL: " + functionUrl);
            var response = await httpClient.PostAsync(functionUrl, content);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Failed to send order OrderDetails to OrderDetailsSaver. Status: {StatusCode}", response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex.Message);
        }
    }

    private async Task SendOrderDetailsToServiceBus(ApplicationCore.Entities.OrderAggregate.Order order, IEnumerable<BasketItemViewModel> items)
    {
        Console.WriteLine("SendOrderToServiceBus");
        var orderRequest = new
        {
            id = Guid.NewGuid().ToString(),
            OrderId = order.Id,
            shippingAddress = new
            {
                Street = order.ShipToAddress.Street,
                City = order.ShipToAddress.City,
                State = order.ShipToAddress.State,
                Country = order.ShipToAddress.Country,
                Zipcode = order.ShipToAddress.ZipCode
            },
            items = order.OrderItems.Select(oi => new { ItemId = oi.Id, Quantity = oi.Units }).ToList(),
            totalPrice = order.OrderItems.Sum(oi => oi.UnitPrice * oi.Units),
            createdAt = DateTime.UtcNow
        };

        try
        {
            var json = JsonSerializer.Serialize(orderRequest);

            var serviceBusConn = _configuration["ConnectionStrings:ServiceBusConnection"]; // app setting
            var queueName = _configuration["servicebus_queue_name"];

            var options = new ServiceBusClientOptions
            {
                TransportType = ServiceBusTransportType.AmqpWebSockets
            };
            await using var client = new ServiceBusClient(serviceBusConn, options);
            ServiceBusSender sender = client.CreateSender(queueName);

            // Create message and send
            ServiceBusMessage message = new ServiceBusMessage(Encoding.UTF8.GetBytes(json))
            {
                ContentType = "application/json",
                Subject = "NewOrder",
                MessageId = orderRequest.id
            };

            await sender.SendMessageAsync(message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex.Message);
        }
    }



}
