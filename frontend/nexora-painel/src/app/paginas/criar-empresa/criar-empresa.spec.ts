import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Login } from '../login/login';
import { CriarEmpresa } from './criar-empresa';

/** CRIAR EMPRESA — a tela do operador, que substituiu um SSH.
 *
 *  ===================== O QUE ESTE ARQUIVO PROTEGE =====================
 *  Quatro coisas, e todas falham em silêncio:
 *
 *    1. a CHAVE vai no cabeçalho `X-Chave-Admin`, e no cabeçalho certo. Errar o nome produz
 *       exatamente o mesmo 401 opaco de chave errada — o operador passaria a tarde conferindo uma
 *       chave que está certa;
 *    2. a chave NÃO fica guardada em lugar nenhum do navegador;
 *    3. o 401 não limpa o formulário. Chave errada não pode custar os dados do cliente já
 *       digitados (a outra metade desta regra está em `interceptor-token.spec.ts`);
 *    4. a tela não é anunciada — nem a de login aponta para cá.
 *  ====================================================================== */
describe('criar empresa (operador)', () => {
  const CHAVE = 'chave-de-administracao-de-teste-0123456789';

  let http: HttpTestingController;
  let fixture: ComponentFixture<CriarEmpresa>;
  let c: CriarEmpresa;

  function montar() {
    fixture = TestBed.createComponent(CriarEmpresa);
    c = fixture.componentInstance;
    fixture.detectChanges();
  }

  /** Preenche tudo com dados válidos. Cada teste altera só o que lhe interessa.
   *
   *  ⚠️ `await`, e não só `detectChanges()`. O `[ngModel]` escreve no input por MICROTASK, então
   *  logo depois do `detectChanges()` o signal já mudou e o DOM ainda não. Sem esperar, todo
   *  `expect` sobre `input.value` lê string vazia — e passa, trivialmente, nos testes em que o
   *  esperado também é vazio. Era exatamente o caso do teste do formulário limpo. */
  async function preencher(over: Partial<Record<string, string>> = {}) {
    c.chave.set(over['chave'] ?? CHAVE);
    c.nome.set(over['nome'] ?? 'Padaria do Zé');
    c.documento.set(over['documento'] ?? '12.345.678/0001-90');
    c.nomeDono.set(over['nomeDono'] ?? 'José Silva');
    c.emailDono.set(over['emailDono'] ?? 'ze@padaria.com.br');
    c.senha.set(over['senha'] ?? 'senhaforte1');
    c.senha2.set(over['senha2'] ?? over['senha'] ?? 'senhaforte1');
    await assentar();
  }

  /** Roda o ciclo até o DOM refletir os signals. */
  async function assentar() {
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
  }

  function enviar() {
    c.criar();
    fixture.detectChanges();
  }

  function pedido() {
    return http.expectOne(r => r.url.endsWith('/cadastro/empresa') && r.method === 'POST');
  }

  function texto(): string { return (fixture.nativeElement as HTMLElement).textContent ?? ''; }

  function valorDe(id: string): string {
    return ((fixture.nativeElement as HTMLElement)
      .querySelector(`#${id}`) as HTMLInputElement | null)?.value ?? '(campo ausente)';
  }

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();

    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    http.verify();
    localStorage.clear();
    sessionStorage.clear();
    TestBed.resetTestingModule();
  });

  // ==================================================================== a credencial
  it('MANDA A CHAVE NO CABEÇALHO X-Chave-Admin, E NÃO NO CORPO', async () => {
    // ⚠️ O LITERAL É AFIRMADO DE PROPÓSITO. O servidor responde a mesma coisa para cabeçalho
    // ausente e chave errada, então um nome errado de cabeçalho é INDISTINGUÍVEL de uma chave
    // errada: o 401 não dedura, e quem depura vai conferir a chave, que está certa.
    montar();
    await preencher();
    enviar();

    const req = pedido();
    expect(req.request.headers.get('X-Chave-Admin')).toBe(CHAVE);
    expect(Object.keys(req.request.body as object))
      .withContext('a chave não pode viajar no corpo').not.toContain('chave');
    req.flush({ empresaId: 1 });
  });

  it('O CORPO LEVA OS CINCO OBRIGATÓRIOS, MESMO EM BRANCO', async () => {
    // Omitir uma `string` não anulável faz o `[ApiController]` responder 400 em
    // ValidationProblemDetails — inglês, moldado pelo framework —, que esta tela não sabe ler.
    // Mandar vazio cai nas validações do serviço, que respondem `{ erro }` em português.
    montar();
    await preencher({ documento: '' });
    enviar();

    const req = pedido();
    const corpo = req.request.body as Record<string, unknown>;
    for (const campo of ['nome', 'documento', 'nomeDono', 'emailDono', 'senha']) {
      expect(Object.keys(corpo)).withContext(`${campo} tem de ir sempre`).toContain(campo);
    }
    expect(corpo['documento']).withContext('CNPJ vazio vira null, não string vazia').toBeNull();
    req.flush({ empresaId: 1 });
  });

  it('NÃO GUARDA A CHAVE DE ADMINISTRAÇÃO EM LUGAR NENHUM', async () => {
    // Afirmação deliberadamente bruta: ela sobrevive a uma refatoração que "ajude" guardando a
    // chave para a próxima empresa. A única persistência do painel é a do `AuthServico`, e esta
    // tela não o toca.
    montar();
    await preencher();
    enviar();
    pedido().flush({ empresaId: 7 });

    expect(JSON.stringify(localStorage)).not.toContain(CHAVE);
    expect(JSON.stringify(sessionStorage)).not.toContain(CHAVE);
  });

  // ==================================================================== o sucesso
  it('O SUCESSO MOSTRA O ID DA EMPRESA E O PRÓXIMO PASSO', async () => {
    // O id é o único identificador durável daqui: é dele que o nome da instância da Evolution é
    // derivado (emp-{id}). Sem ele o operador não acha este cliente em lugar nenhum.
    montar();
    await preencher();
    enviar();
    pedido().flush({ empresaId: 42 });
    fixture.detectChanges();

    expect(texto()).toContain('42');
    expect(texto()).withContext('o operador precisa saber o que repassar').toContain('QR');
    expect(texto()).toContain('ze@padaria.com.br');
  });

  it('DEPOIS DO SUCESSO O FORMULÁRIO VOLTA VAZIO, INCLUSIVE A CHAVE', async () => {
    montar();
    await preencher();
    enviar();
    pedido().flush({ empresaId: 7 });
    fixture.detectChanges();

    c.outra();
    await assentar();

    expect(valorDe('chave')).withContext('a chave é um segredo vivo enquanto a aba fica aberta').toBe('');
    expect(valorDe('nome')).toBe('');
    expect(valorDe('emailDono')).toBe('');
    expect(valorDe('senha')).toBe('');
  });

  // ==================================================================== os erros
  it('O 401 DIZ O QUE FAZER SEM AFIRMAR SE A CHAVE FALTOU OU ESTÁ ERRADA', async () => {
    montar();
    await preencher();
    enviar();
    pedido().flush({ erro: 'Não autorizado.' }, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    const t = texto();
    expect(t).withContext('"Não autorizado." sozinho não dá o que fazer').toContain('chave');
    expect(t).withContext('o cadastro desligado no servidor é a causa que ninguém adivinha')
      .toContain('DESLIGADO');
    expect(t).withContext('sem isto o operador fica em dúvida se existe meia empresa')
      .toContain('Nada foi criado');
  });

  it('O 401 NÃO LIMPA O FORMULÁRIO — CHAVE ERRADA NÃO PODE CUSTAR OS DADOS DO CLIENTE', async () => {
    montar();
    await preencher();
    enviar();
    pedido().flush({ erro: 'Não autorizado.' }, { status: 401, statusText: 'Unauthorized' });
    // ⚠️ `assentar()`, e não `detectChanges()` sozinho. O `[ngModel]` propaga por microtask, então
    // logo depois do flush o input ainda guarda o valor ANTERIOR. Sem esperar, este teste passa
    // mesmo com o formulário sendo limpo — ele leria o DOM velho. Foi o que a sabotagem pegou.
    await assentar();

    expect(valorDe('nome')).toBe('Padaria do Zé');
    expect(valorDe('emailDono')).toBe('ze@padaria.com.br');
    expect(valorDe('chave')).withContext('a chave fica: o provável é colagem pela metade').toBe(CHAVE);
  });

  it('O 409 MOSTRA A MENSAGEM DO SERVIDOR, QUE JÁ É ACIONÁVEL', async () => {
    // Não há campo `conflito` no corpo — o `FiltroRegraDeNegocio` o usa para escolher 409 e nunca
    // o serializa. Quem decide é o status.
    montar();
    await preencher();
    enviar();
    pedido().flush({ erro: 'Já existe usuário com este e-mail.' },
      { status: 409, statusText: 'Conflict' });
    fixture.detectChanges();

    expect(texto()).toContain('Já existe usuário com este e-mail.');
  });

  it('O 429 MOSTRA QUANTO ESPERAR, COM O NÚMERO QUE O SERVIDOR MANDOU', async () => {
    montar();
    await preencher();
    enviar();
    pedido().flush({ erro: 'Muitas tentativas. Aguarde 900 segundos e tente novamente.' },
      { status: 429, statusText: 'Too Many Requests' });
    fixture.detectChanges();

    expect(texto()).toContain('900');
  });

  it('O 400 SEM CORPO LEGÍVEL NÃO DESPEJA ProblemDetails NA TELA', async () => {
    // Model binding devolve `{ title, status, errors }` em inglês. Renderizar aquilo é pior que
    // não dizer nada: o operador lê "The Nome field is required" e não sabe o que é "Nome field".
    montar();
    await preencher();
    enviar();
    pedido().flush({ title: 'One or more validation errors occurred.', status: 400, errors: {} },
      { status: 400, statusText: 'Bad Request' });
    fixture.detectChanges();

    expect(texto()).toContain('confira os campos');
    expect(texto()).not.toContain('validation errors');
  });

  it('FALHA DE REDE AVISA QUE A EMPRESA PODE TER SIDO CRIADA', async () => {
    // A única mensagem hedge da tela, e a que um revisor "limparia". O envio não é idempotente:
    // repetir criaria uma segunda empresa, se não fosse a unicidade global do e-mail.
    montar();
    await preencher();
    enviar();
    pedido().error(new ProgressEvent('error'));
    fixture.detectChanges();

    expect(texto()).toContain('pode ter sido criada');
  });

  // ==================================================================== o que nem sai daqui
  it('SENHA CURTA E SENHAS DIFERENTES NÃO CHEGAM A SAIR DO NAVEGADOR', async () => {
    // As duas frases são as mesmas do `redefinir.ts:40-41`, palavra por palavra. A primeira é
    // idêntica à do servidor, então o operador lê a mesma coisa venha de onde vier.
    montar();

    await preencher({ senha: 'curta1' });
    enviar();
    expect(texto()).toContain('A senha precisa de ao menos 8 caracteres.');

    await preencher({ senha: 'senhaforte1', senha2: 'outracoisa1' });
    enviar();
    expect(texto()).toContain('As senhas não conferem.');

    http.expectNone(() => true);
  });

  it('SEM CHAVE, NÃO ENVIA NADA', async () => {
    // Uma requisição sem o cabeçalho gastaria um dos 3 cadastros por hora em nada.
    montar();
    await preencher({ chave: '' });

    const botao = (fixture.nativeElement as HTMLElement)
      .querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(botao.disabled).toBeTrue();

    enviar();
    http.expectNone(() => true);
  });

  // ==================================================================== não anunciada
  it('A TELA DE LOGIN NÃO TEM LINK PARA CÁ', async () => {
    // A outra metade está em `navegacao.spec.ts` (o menu do painel). Mora aqui para aquela suíte
    // não ganhar um import do `Login` só por causa disto.
    const f = TestBed.createComponent(Login);
    f.detectChanges();

    expect((f.nativeElement as HTMLElement).querySelectorAll('a[href="/criar-empresa"]').length)
      .toBe(0);
  });
});
