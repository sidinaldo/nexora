import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { Comecar } from './comecar';

/** PRIMEIROS PASSOS — quantos passos há vem do servidor (AUD-XX, #27). */
describe('primeiros passos', () => {
  afterEach(() => TestBed.resetTestingModule());

  function passo(chave: 'conexao' | 'equipe' | 'primeira_mensagem' | 'anuncios', concluido: boolean) {
    return {
      chave, titulo: chave, descricao: '', concluido, dispensado: false, rota: null, rotuloAcao: null
    };
  }

  /** O texto dizia "Três passos" fixo, e o checklist pode ter quatro (o de anúncios só aparece
   *  para quem tem anúncio chegando). */
  it('O NÚMERO DE PASSOS É O DO SERVIDOR, e não "três" escrito na tela', () => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideRouter([]),
        provideHttpClient(),
        provideHttpClientTesting()
      ]
    });

    const fixture = TestBed.createComponent(Comecar);
    fixture.detectChanges();

    TestBed.inject(HttpTestingController).expectOne(r => r.url.endsWith('/onboarding')).flush({
      passos: [
        passo('conexao', true), passo('equipe', false),
        passo('primeira_mensagem', false), passo('anuncios', false)
      ],
      concluidos: 1, total: 4, completo: false, dispensado: false, mostrar: true,
      minutosAteAPrimeiraMensagem: null
    });
    fixture.detectChanges();

    const texto = (fixture.nativeElement as HTMLElement).textContent!.replace(/\s+/g, ' ');
    expect(texto).toContain('4 passos para o Nexora começar a trabalhar por você.');
    expect(texto).not.toContain('Três passos');
  });
});
