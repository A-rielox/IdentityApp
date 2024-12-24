using Api.DTOs.Account;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Threading.Tasks;

namespace Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AccountController : ControllerBase
{
    private readonly JWTService _jwtService;
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;

    public AccountController(   JWTService jwtService,
                                SignInManager<User> signInManager,
                                UserManager<User> userManager)
    {
        _jwtService = jwtService;
        _signInManager = signInManager;
        _userManager = userManager;
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////

    [Authorize]
    [HttpGet("refresh-user-token")]
    public async Task<ActionResult<UserDto>> RefreshUserToken()
    {
        var user = await _userManager.FindByNameAsync(User.FindFirst(ClaimTypes.Email)?.Value); // en el UserName tengo el email
        return CreateApplicationUserDto(user);
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////

    [HttpPost("login")]
    public async Task<ActionResult<UserDto>> Login(LoginDto model)
    {
        var user = await _userManager.FindByNameAsync(model.UserName);
        if (user == null) return Unauthorized("Invalid username or password");

        if (user.EmailConfirmed == false) return Unauthorized("Please confirm you email.");

        var result = await _signInManager.CheckPasswordSignInAsync(user, model.Password, false);
        if (!result.Succeeded) return Unauthorized("Invalid username or password");

        return CreateApplicationUserDto(user);
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterDto model)
    {
        if (await CheckEmailExistsAsync(model.Email))
        {
            return BadRequest($"An existing account is using {model.Email}, email addres. Please try with another email address");
        }

        var userToAdd = new User
        {
            FirstName = model.FirstName.ToLower(),
            LastName = model.LastName.ToLower(),
            UserName = model.Email.ToLower(),//<<-----------    pone el email
            Email = model.Email.ToLower(),
            EmailConfirmed = true
        };

        // creates a user inside our AspNetUsers table inside our database
        var result = await _userManager.CreateAsync(userToAdd, model.Password);
        if (!result.Succeeded) return BadRequest(result.Errors);

        return Ok(new JsonResult(new { title = "Account Created", message = "Your account has been created, you can login" }));
        // la respuesta que va a recibir angular va a ser un object q va a tener:
        // value: { title: 'Account Created', message: 'Your account has been created, you can login'}
        // lo hace p' pasar esto directo al modal de cuenta creada
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////

    #region Private Helper Methods
    private UserDto CreateApplicationUserDto(User user)
    {
        return new UserDto
        {
            FirstName = user.FirstName,
            LastName = user.LastName,
            JWT = _jwtService.CreateJWT(user),
        };
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////

    private async Task<bool> CheckEmailExistsAsync(string email)
    {
        return await _userManager.Users.AnyAsync(x => x.Email == email.ToLower());
    }
    #endregion
}
