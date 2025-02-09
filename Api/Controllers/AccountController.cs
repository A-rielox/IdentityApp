﻿using Api.DTOs.Account;
using Api.Models;
using Api.Services;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;

namespace Api.Controllers;

[Route("api/[controller]")]
[ApiController]
public class AccountController : ControllerBase
{
    private readonly JWTService _jwtService;
    private readonly SignInManager<User> _signInManager;
    private readonly UserManager<User> _userManager;
    private readonly EmailService _emailService;
    private readonly IConfiguration _config;
    private readonly HttpClient _facebookHttpClient;

    public AccountController(   JWTService jwtService,
                                SignInManager<User> signInManager,
                                UserManager<User> userManager,
                                EmailService emailService,
                                IConfiguration config)
    {
        _jwtService = jwtService;
        _signInManager = signInManager;
        _userManager = userManager;
        _emailService = emailService;
        _config = config;
        _facebookHttpClient = new HttpClient
        {
            BaseAddress = new Uri("https://graph.facebook.com")
        };
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    /*
    let headers = new HttpHeaders();
    headers = headers.set('Authorization', 'Bearer ' + jwt);

    return this.http
      .get<User>(`${environment.appUrl}/api/account/refresh-user-token`, { headers })
      .pipe( ...
    */

    [Authorize] // angular va a mandar directo un request con un header con Bearer token ...
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


    [HttpPost("login-with-third-party")]
    public async Task<ActionResult<UserDto>> LoginWithThirdParty(LoginWithExternalDto model)
    {
        if (model.Provider.Equals(SD.Facebook))
        {
            try
            {
                if (!FacebookValidatedAsync(model.AccessToken, model.UserId).GetAwaiter().GetResult())
                {
                    return Unauthorized("Unable to login with facebook");
                }
            }
            catch (Exception)
            {
                return Unauthorized("Unable to login with facebook");
            }
        }
        else if (model.Provider.Equals(SD.Google))
        {
            try
            {
                if (!GoogleValidatedAsync(model.AccessToken, model.UserId).GetAwaiter().GetResult())
                {
                    return Unauthorized("Unable to login with google");
                }
            }
            catch (Exception)
            {
                return Unauthorized("Unable to login with google");
            }
        }
        else
        {
            return BadRequest("Invalid provider");
        }

        var user = await _userManager.Users.FirstOrDefaultAsync(x => x.UserName == model.UserId && x.Provider == model.Provider);
        if (user == null) return Unauthorized("Unable to find your account");

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
            //EmailConfirmed = true
        };

        // creates a user inside our AspNetUsers table inside our database
        var result = await _userManager.CreateAsync(userToAdd, model.Password);
        if (!result.Succeeded) return BadRequest(result.Errors);

        try
        {
            if (await SendConfirmEMailAsync(userToAdd))
            {
                return Ok(new JsonResult(new { title = "Account Created", message = "Your account has been created, please confrim your email address" }));
            }

            return BadRequest("Failed to send email. Please contact admin");
        }
        catch (Exception)
        {
            return BadRequest("Failed to send email. Please contact admin");
        }

        // return Ok(new JsonResult(new { title = "Account Created", message = "Your account has been created, you can login" }));
        // la respuesta que va a recibir angular va a ser un object q va a tener:
        // value: { title: 'Account Created', message: 'Your account has been created, you can login'}
        // lo hace p' pasar esto directo al modal de cuenta creada
    }

    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    [HttpPost("register-with-third-party")]
    public async Task<ActionResult<UserDto>> RegisterWithThirdParty(RegisterWithExternal model)
    {
        if (model.Provider.Equals(SD.Facebook))
        {
            try
            {
                if (!FacebookValidatedAsync(model.AccessToken, model.UserId).GetAwaiter().GetResult())
                {
                    return Unauthorized("Unable to register with facebook");
                }
            }
            catch (Exception)
            {
                return Unauthorized("Unable to register with facebook");
            }
        }
        else if (model.Provider.Equals(SD.Google))
        {
            try
            {
                if (!GoogleValidatedAsync(model.AccessToken, model.UserId).GetAwaiter().GetResult())
                {
                    return Unauthorized("Unable to register with google");
                }
            }
            catch (Exception)
            {
                return Unauthorized("Unable to register with google");
            }
        }
        else
        {
            return BadRequest("Invalid provider");
        }

        var user = await _userManager.FindByNameAsync(model.UserId);
        if (user != null) return BadRequest(string.Format("You have an account already. Please login with your {0}", model.Provider));

        var userToAdd = new User
        {
            FirstName = model.FirstName.ToLower(),
            LastName = model.LastName.ToLower(),
            UserName = model.UserId,
            Provider = model.Provider,
        };

        var result = await _userManager.CreateAsync(userToAdd);
        if (!result.Succeeded) return BadRequest(result.Errors);

        return CreateApplicationUserDto(userToAdd);
    }




    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    [HttpPut("confirm-email")]
    public async Task<IActionResult> ConfirmEmail(ConfirmEmailDto model)
    {
        var user = await _userManager.FindByEmailAsync(model.Email);

        if (user == null) return Unauthorized("This email address has not been registered yet");

        if (user.EmailConfirmed == true) return BadRequest("Your email was confirmed before. Please login to your account");

        try
        {
            var decodedTokenBytes = WebEncoders.Base64UrlDecode(model.Token);
            var decodedToken = Encoding.UTF8.GetString(decodedTokenBytes);
            var result = await _userManager.ConfirmEmailAsync(user, decodedToken);

            if (result.Succeeded)
            {
                return Ok(new JsonResult(new { title = "Email confirmed", message = "Your email address is confirmed. You can login now" }));
            }

            return BadRequest("Invalid token. Please try again");
        }
        catch (Exception)
        {
            return BadRequest("Invalid token. Please try again");
        }
    }



    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    [HttpPost("resend-email-confirmation-link/{email}")]
    public async Task<IActionResult> ResendEMailConfirmationLink(string email)
    {
        if (string.IsNullOrEmpty(email)) return BadRequest("Invalid email");

        var user = await _userManager.FindByEmailAsync(email);

        if (user == null) return Unauthorized("This email address has not been registerd yet");
        if (user.EmailConfirmed == true) return BadRequest("Your email address was confirmed before. Please login to your account");

        try
        {
            if (await SendConfirmEMailAsync(user))
            {
                return Ok(new JsonResult(new { title = "Confirmation link sent", message = "Please confrim your email address" }));
            }

            return BadRequest("Failed to send email. PLease contact admin");
        }
        catch (Exception)
        {
            return BadRequest("Failed to send email. PLease contact admin");
        }
    }



    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    [HttpPost("forgot-username-or-password/{email}")]
    public async Task<IActionResult> ForgotUsernameOrPassword(string email)
    {
        if (string.IsNullOrEmpty(email)) return BadRequest("Invalid email");

        var user = await _userManager.FindByEmailAsync(email);

        if (user == null) return Unauthorized("This email address has not been registerd yet");
        if (user.EmailConfirmed == false) return BadRequest("Please confirm your email address first.");

        try
        {
            if (await SendForgotUsernameOrPasswordEmail(user))
            {
                return Ok(new JsonResult(new { title = "Forgot username or password email sent", message = "Please check your email" }));
            }

            return BadRequest("Failed to send email. Please contact admin");
        }
        catch (Exception)
        {
            return BadRequest("Failed to send email. Please contact admin");
        }
    }



    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    [HttpPut("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordDto model)
    {
        var user = await _userManager.FindByEmailAsync(model.Email);

        if (user == null) return Unauthorized("This email address has not been registerd yet");
        if (user.EmailConfirmed == false) return BadRequest("PLease confirm your email address first");

        try
        {
            var decodedTokenBytes = WebEncoders.Base64UrlDecode(model.Token);
            var decodedToken = Encoding.UTF8.GetString(decodedTokenBytes);

            var result = await _userManager.ResetPasswordAsync(user, decodedToken, model.NewPassword);
            if (result.Succeeded)
            {
                return Ok(new JsonResult(new { title = "Password reset success", message = "Your password has been reset" }));
            }

            return BadRequest("Invalid token. Please try again");
        }
        catch (Exception)
        {
            return BadRequest("Invalid token. Please try again");
        }
    }








    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
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
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////    
    private async Task<bool> SendConfirmEMailAsync(User user)
    {
        var token = await _userManager.GenerateEmailConfirmationTokenAsync(user);

        token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var url = $"{_config["JWT:ClientUrl"]}/{_config["Email:ConfirmEmailPath"]}?token={token}&email={user.Email}";

        var body = $"<p>Hello: {user.FirstName} {user.LastName}</p>" +
                    "<p>Please confirm your email address by clicking on the following link.</p>" +
                    $"<p><a href=\"{url}\">Click here</a></p>" +
                    "<p>Thank you,</p>" +
                    $"<br>{_config["Email:ApplicationName"]}";

        var emailSend = new EmailSendDto(user.Email, "Confirm your email", body);

        return await _emailService.SendEmailAsync(emailSend);
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    private async Task<bool> SendForgotUsernameOrPasswordEmail(User user)
    {
        var token = await _userManager.GeneratePasswordResetTokenAsync(user);
        token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token));

        var url = $"{_config["JWT:ClientUrl"]}/{_config["Email:ResetPasswordPath"]}?token={token}&email={user.Email}";

        var body = $"<p>Hello: {user.FirstName} {user.LastName}</p>" +
           $"<p>Username: {user.UserName}.</p>" +
           "<p>In order to reset your password, please click on the following link.</p>" +
           $"<p><a href=\"{url}\">Click here</a></p>" +
           "<p>Thank you,</p>" +
           $"<br>{_config["Email:ApplicationName"]}";

        var emailSend = new EmailSendDto(user.Email, "Forgot username or password", body);

        return await _emailService.SendEmailAsync(emailSend);
    }
    /////////////////////////////////////////////////////////////
    ////////////////////////////////////////////////////////////////
    private async Task<bool> FacebookValidatedAsync(string accessToken, string userId)
    {
        var facebookKeys = _config["Facebook:AppId"] + "|" + _config["Facebook:AppSecret"];
        var fbResult = await _facebookHttpClient.GetFromJsonAsync<FacebookResultDto>($"debug_token?input_token={accessToken}&access_token={facebookKeys}");

        if (fbResult == null || fbResult.Data.Is_Valid == false || !fbResult.Data.User_Id.Equals(userId))
        {
            return false;
        }

        return true;
    }

    private async Task<bool> GoogleValidatedAsync(string accessToken, string userId)
    {
        var payload = await GoogleJsonWebSignature.ValidateAsync(accessToken);

        if (!payload.Audience.Equals(_config["Google:ClientId"]))
        {
            return false;
        }

        if (!payload.Issuer.Equals("accounts.google.com") && !payload.Issuer.Equals("https://accounts.google.com"))
        {
            return false;
        }

        if (payload.ExpirationTimeSeconds == null)
        {
            return false;
        }

        DateTime now = DateTime.Now.ToUniversalTime();
        DateTime expiration = DateTimeOffset.FromUnixTimeSeconds((long)payload.ExpirationTimeSeconds).DateTime;

        if (now > expiration)
        {
            return false;
        }

        if (!payload.Subject.Equals(userId))
        {
            return false;
        }

        return true;
    }
    #endregion
}
