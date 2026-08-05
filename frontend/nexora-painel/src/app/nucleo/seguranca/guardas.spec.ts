import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection, runInInjectionContext, EnvironmentInjector } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthServico } from '../servicos/auth.servico';
import { guardaAutenticado, guardaDono, guardaGestor } from './guardas';

/** Os guards são conveniência de UX — o enforcement real é o `[Authorize(Roles=...)]` no
 *  controller. Mas conveniência que falha ABRINDO é outra coisa: a tela de equipe carregaria
 *  para um vendedor e só quebraria no 403, depois de já ter mostrado o que não devia. */
describe('guardas de rota', () => {
  let auth: AuthServico;
  let injector: EnvironmentInjector;

  beforeEach(() => {
    localStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([])
      ]
    });

    auth = TestBed.inject(AuthServico);
    injector = TestBed.inject(EnvironmentInjector);
  });

  afterEach(() => localStorage.clear());

  /** CanActivateFn usa `inject()`, então precisa rodar dentro de um contexto de injeção. */
  function rodar(guarda: typeof guardaDono): boolean | UrlTree {
    return runInInjectionContext(injector, () =>
      guarda({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot)) as boolean | UrlTree;
  }

  function entrarComo(papel: 'dono' | 'gestor' | 'vendedor') {
    auth.aplicarLogin({
      token: 't',
      usuario: { id: 1, nome: 'X', email: 'x@x.com', papel, empresaNome: 'E' }
    } as never);
  }

  /** O guard não devolve `false`: devolve uma UrlTree, que é o redirecionamento. Um `false`
   *  seco deixaria o usuário parado na rota anterior, sem explicação. */
  function destinoDoRedirecionamento(r: boolean | UrlTree): string {
    expect(r instanceof UrlTree).withContext('deveria redirecionar, não devolver booleano').toBeTrue();
    return (r as UrlTree).toString();
  }

  describe('guardaAutenticado', () => {
    it('deixa passar com sessão', () => {
      entrarComo('vendedor');
      expect(rodar(guardaAutenticado)).toBeTrue();
    });

    it('sem sessão, manda para o login', () => {
      expect(destinoDoRedirecionamento(rodar(guardaAutenticado))).toBe('/entrar');
    });
  });

  describe('guardaDono', () => {
    it('deixa passar o dono', () => {
      entrarComo('dono');
      expect(rodar(guardaDono)).toBeTrue();
    });

    it('BARRA gestor e vendedor, mandando para a caixa', () => {
      for (const papel of ['gestor', 'vendedor'] as const) {
        entrarComo(papel);
        expect(destinoDoRedirecionamento(rodar(guardaDono)))
          .withContext(`${papel} não pode abrir tela de dono`).toBe('/caixa');
      }
    });

    it('barra quem não tem sessão nenhuma', () => {
      expect(destinoDoRedirecionamento(rodar(guardaDono))).toBe('/caixa');
    });
  });

  describe('guardaGestor', () => {
    it('deixa passar dono e gestor', () => {
      for (const papel of ['dono', 'gestor'] as const) {
        entrarComo(papel);
        expect(rodar(guardaGestor)).withContext(papel).toBeTrue();
      }
    });

    it('barra vendedor', () => {
      entrarComo('vendedor');
      expect(destinoDoRedirecionamento(rodar(guardaGestor))).toBe('/caixa');
    });
  });

  it('papel desconhecido não vira dono por acidente', () => {
    // Se um papel novo entrar no backend e ninguém atualizar o cliente, o padrão tem que ser
    // NEGAR. Um `!== 'vendedor'` em vez de `=== 'dono'` abriria a porta sozinho.
    entrarComo('supervisor' as never);
    expect(destinoDoRedirecionamento(rodar(guardaDono))).toBe('/caixa');
    expect(destinoDoRedirecionamento(rodar(guardaGestor))).toBe('/caixa');
    expect(rodar(guardaAutenticado)).toBeTrue();   // mas continua sendo uma sessão válida
  });
});
